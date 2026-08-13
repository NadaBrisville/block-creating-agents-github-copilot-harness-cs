using System;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Newtonsoft.Json.Linq;

namespace BotCreationGuard
{
    public class BotCreationGuardPlugin : IPlugin
    {
        // The pair that identifies a CLI / GitHub Copilot harness agent.
        private const string RestrictedTemplate = "cliagent-1.0.0";
        private const string RestrictedRecognizerKind = "CLICopilotRecognizer";

        // Schema name of the Dataverse Environment Variable holding the exact name of the
        // restricted security role. Change the prefix if your publisher is not "nadabr_" --
        // it is baked into the compiled assembly and cannot be resolved at runtime.
        private const string EnvironmentVariableSchemaName = "nadabr_RestrictedRoleName";

        public void Execute(IServiceProvider serviceProvider)
        {
            ITracingService tracingService =
                (ITracingService)serviceProvider.GetService(typeof(ITracingService));

            IPluginExecutionContext context =
                (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));

            IOrganizationServiceFactory serviceFactory =
                (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));

            // System context (null userId) rather than the caller's: reading the guard's own
            // configuration must not depend on the maker's privileges. A maker without read
            // access to Environment Variables or to security roles would otherwise trip the
            // fail-safe path and bypass the block silently.
            IOrganizationService service =
                serviceFactory.CreateOrganizationService(null);

            tracingService.Trace("BotCreationGuardPlugin: Execute started.");

            if (context.MessageName != "Create" || context.Stage != 20)
            {
                return;
            }

            if (!context.InputParameters.Contains("Target") ||
                !(context.InputParameters["Target"] is Entity))
            {
                return;
            }

            Entity targetBot = (Entity)context.InputParameters["Target"];

            if (targetBot.LogicalName != "bot")
            {
                return;
            }

            // 1. Check the template.
            string template = targetBot.GetAttributeValue<string>("template");
            tracingService.Trace("Template: " + (template ?? "null"));

            if (string.IsNullOrEmpty(template) || template != RestrictedTemplate)
            {
                tracingService.Trace("Template does not match restricted template, allowing.");
                return;
            }

            // 2. Check the recognizer inside the configuration column (JSON).
            string configurationJson = targetBot.GetAttributeValue<string>("configuration");

            if (string.IsNullOrEmpty(configurationJson))
            {
                tracingService.Trace("No configuration found, allowing.");
                return;
            }

            string recognizerKind = null;
            try
            {
                JObject config = JObject.Parse(configurationJson);
                recognizerKind = config["recognizer"]?["$kind"]?.ToString();
            }
            catch (Exception ex)
            {
                tracingService.Trace("Failed to parse configuration JSON: " + ex.Message);
                return; // malformed JSON must not get in the way of a creation
            }

            tracingService.Trace("Recognizer kind: " + (recognizerKind ?? "null"));

            if (string.IsNullOrEmpty(recognizerKind) || recognizerKind != RestrictedRecognizerKind)
            {
                tracingService.Trace("Recognizer does not match, allowing.");
                return;
            }

            // 3. Read the restricted role name from the Dataverse Environment Variable.
            //    Fail-safe: with nothing configured, let the creation through rather than
            //    blocking every maker in the environment.
            string restrictedRoleName = GetRestrictedRoleName(service, tracingService);

            if (string.IsNullOrEmpty(restrictedRoleName))
            {
                tracingService.Trace("No restricted role configured, allowing.");
                return;
            }

            // 4. Check that the initiating user actually holds the restricted role.
            bool userHasRestrictedRole = UserHasRole(service, context.InitiatingUserId, restrictedRoleName, tracingService);

            if (!userHasRestrictedRole)
            {
                tracingService.Trace("User does not have the restricted role, allowing.");
                return;
            }

            // Every condition is met: block.
            tracingService.Trace("Blocking creation: restricted template + recognizer + role match.");

            throw new InvalidPluginExecutionException(
                "Agent creation blocked: agents using the CLI/GitHub Copilot harness " +
                "(template 'cliagent-1.0.0' with CLICopilotRecognizer) cannot be created " +
                "by users assigned the '" + restrictedRoleName + "' role. " +
                "Contact your Dataverse administrator if you need to create this type of agent."
            );
        }

        /// <summary>
        /// Returns the restricted security role name, read from the Environment Variable named
        /// by <see cref="EnvironmentVariableSchemaName"/>. The current value
        /// (environmentvariablevalue) wins over the definition's default value.
        /// Returns null when the variable is missing or carries no value at all, in which case
        /// no restriction is applied -- a deliberate fail-safe.
        /// </summary>
        private string GetRestrictedRoleName(IOrganizationService service, ITracingService tracingService)
        {
            QueryExpression query = new QueryExpression("environmentvariabledefinition")
            {
                ColumnSet = new ColumnSet("environmentvariabledefinitionid", "defaultvalue"),
                Criteria = new FilterExpression
                {
                    Conditions =
                    {
                        new ConditionExpression("schemaname", ConditionOperator.Equal, EnvironmentVariableSchemaName)
                    }
                }
            };

            LinkEntity linkToValue = new LinkEntity(
                "environmentvariabledefinition", "environmentvariablevalue",
                "environmentvariabledefinitionid", "environmentvariabledefinitionid",
                JoinOperator.LeftOuter);

            linkToValue.EntityAlias = "val";
            linkToValue.Columns = new ColumnSet("value");
            query.LinkEntities.Add(linkToValue);

            EntityCollection result;
            try
            {
                result = service.RetrieveMultiple(query);
            }
            catch (Exception ex)
            {
                tracingService.Trace("Failed to read environment variable: " + ex.Message);
                return null;
            }

            if (result.Entities.Count == 0)
            {
                tracingService.Trace("Environment variable definition '" + EnvironmentVariableSchemaName +
                                     "' not found, skipping restriction.");
                return null;
            }

            Entity record = result.Entities[0];

            AliasedValue aliased = record.GetAttributeValue<AliasedValue>("val.value");
            string value = aliased == null ? null : aliased.Value as string;

            if (string.IsNullOrEmpty(value))
            {
                // No environment-specific value: fall back to the definition's default value.
                // Default values travel with a solution, current values do not.
                value = record.GetAttributeValue<string>("defaultvalue");
            }

            if (string.IsNullOrEmpty(value))
            {
                tracingService.Trace("No environment variable value set, skipping restriction.");
                return null;
            }

            tracingService.Trace("Restricted role name resolved to: " + value);

            return value;
        }

        /// <summary>
        /// Whether the user holds <paramref name="roleName"/>, either assigned directly or
        /// inherited from a team they belong to. Both paths count: an Entra group team grants
        /// its roles to members without ever writing a row in systemuserroles.
        /// </summary>
        private bool UserHasRole(IOrganizationService service, Guid userId, string roleName, ITracingService tracingService)
        {
            if (UserHasRoleDirectly(service, userId, roleName, tracingService))
            {
                return true;
            }

            return UserHasRoleViaTeam(service, userId, roleName, tracingService);
        }

        /// <summary>
        /// Role assigned directly to the user (systemuserroles intersect table).
        /// </summary>
        private bool UserHasRoleDirectly(IOrganizationService service, Guid userId, string roleName, ITracingService tracingService)
        {
            QueryExpression query = BuildRoleQuery(roleName);

            LinkEntity linkToUserRoles = new LinkEntity(
                "role", "systemuserroles",
                "roleid", "roleid",
                JoinOperator.Inner);

            linkToUserRoles.LinkCriteria.AddCondition(
                "systemuserid", ConditionOperator.Equal, userId);

            query.LinkEntities.Add(linkToUserRoles);

            bool found = service.RetrieveMultiple(query).Entities.Count > 0;

            tracingService.Trace("Direct role assignment found: " + found);

            return found;
        }

        /// <summary>
        /// Role inherited from a team: role -> teamroles -> teammembership.
        /// </summary>
        private bool UserHasRoleViaTeam(IOrganizationService service, Guid userId, string roleName, ITracingService tracingService)
        {
            QueryExpression query = BuildRoleQuery(roleName);

            LinkEntity linkToTeamRoles = new LinkEntity(
                "role", "teamroles",
                "roleid", "roleid",
                JoinOperator.Inner);

            LinkEntity linkToTeamMembership = new LinkEntity(
                "teamroles", "teammembership",
                "teamid", "teamid",
                JoinOperator.Inner);

            linkToTeamMembership.LinkCriteria.AddCondition(
                "systemuserid", ConditionOperator.Equal, userId);

            linkToTeamRoles.LinkEntities.Add(linkToTeamMembership);
            query.LinkEntities.Add(linkToTeamRoles);

            bool found = service.RetrieveMultiple(query).Entities.Count > 0;

            tracingService.Trace("Team-inherited role assignment found: " + found);

            return found;
        }

        /// <summary>
        /// Base query over roles carrying the given name. Several records can share that name,
        /// one per business unit; any match will do, hence TopCount = 1.
        /// </summary>
        private static QueryExpression BuildRoleQuery(string roleName)
        {
            return new QueryExpression("role")
            {
                ColumnSet = new ColumnSet("name"),
                TopCount = 1,
                Criteria = new FilterExpression
                {
                    Conditions =
                    {
                        new ConditionExpression("name", ConditionOperator.Equal, roleName)
                    }
                }
            };
        }
    }
}
