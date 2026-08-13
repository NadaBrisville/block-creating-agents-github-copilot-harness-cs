# BotCreationGuard

A Dataverse plug-in that blocks the creation of Copilot Studio agents built on the
**CLI / GitHub Copilot harness** for a restricted population of makers.

Microsoft does not expose a setting to disable this specific agent-creation path. This plug-in
fills the gap: it intercepts the `Create` message on the `bot` table in **Pre-Operation** and
throws when three conditions are met at once.

> **Just want it running?** Follow the
> [Step by step installation](STEP-BY-STEP-INSTALLATION.md) guide instead. Download the solution,
> import it, assign the role. No build, no Plugin Registration Tool, no code.
> The rest of this README is for people who want to understand or modify how it works.

**The full write-up is on Medium:
[Blocking GitHub Copilot Harness Agents in Copilot Studio](https://medium.com/@nada.brisville/blocking-github-copilot-harness-agents-in-copilot-studio-777f96a9e9c9)**,
covering why this harness needs its own control, how the signal was traced back to the `bot` table,
and why security roles alone cannot enforce it.

## How it works

The creation is blocked only if **all three** conditions are true:

| # | Condition | Where it is read |
|---|-----------|------------------|
| 1 | `template` equals `cliagent-1.0.0` | `bot.template` |
| 2 | `recognizer.$kind` equals `CLICopilotRecognizer` | `bot.configuration` (JSON) |
| 3 | The initiating user holds the restricted security role | `role` ⋈ `systemuserroles`, or `role` ⋈ `teamroles` ⋈ `teammembership` |

Anything else — a regular agent, a different template, a user without the role — passes through
untouched.

`template` identifies the agent type. Other values you will see in the same column, useful if you
want to retarget the plug-in at a different harness:

| `template` | Agent type |
|---|---|
| `cliagent-1.0.0` | CLI / GitHub Copilot harness |
| `default-2.1.0` | standard generative agent |
| `powerpages-1.0.0` | Power Pages agent |
| `gpt-1.1.0` | legacy Custom GPT |

The name of the restricted security role is **not hard-coded**. It is read at runtime from a
Dataverse **Environment Variable**, so the same solution can be imported into any tenant without
recompiling, whatever the role is called there.

The role check counts **both** direct assignments and roles inherited from a team the user belongs
to. Microsoft Entra group teams grant their roles to members without ever writing a row in
`systemuserroles`, so checking direct assignments alone would let every group-managed user through.

Configuration is read under the **system user context**, not the caller's. A maker who lacks read
privileges on the environment variable or on security roles would otherwise trip the fail-safe and
bypass the guard silently.

Holding **System Administrator does not exempt anyone**. The plug-in checks only for the presence
of the restricted role. Administrators do hold the *Bypass Custom Business Logic* privilege, but
that privilege only takes effect when the caller explicitly asks for the bypass on the request,
which Copilot Studio does not do.

### Fail-safe behaviour

If the environment variable definition does not exist, has no value, has no default value, or
cannot be read, the plug-in **lets the creation through** and traces the reason. A configuration
mistake must never block every maker in the environment.

The trade-off is deliberate but worth stating plainly: **every failure mode of this plug-in is
silent and permissive.** A misconfiguration looks exactly like a working installation.

## Project structure

```
BotCreationGuard/
├── BotCreationGuard.csproj      # net462, signed assembly
├── BotCreationGuardPlugin.cs    # the plug-in
├── README.md
├── LICENSE
└── .gitignore
```

Not in the repository, by design:

- `bin/`, `obj/` — build output
- `BotCreationGuard.snk` — the strong-name signing key (generate your own, see below)
- `*.zip` — the exported solution, published as a [GitHub Release](../../releases) asset instead

## Prerequisites

- .NET SDK (the project targets `net462`, which requires the .NET Framework 4.6.2 targeting pack
  on Windows)
- [Power Platform CLI](https://learn.microsoft.com/power-platform/developer/cli/introduction)
  (`pac`) for the Plugin Registration Tool
- Privileges to register plug-ins in the target environment. **System Administrator** covers it.
  Otherwise the importing account needs at least **Create** on `Plug-in Assembly`, `Plug-in Type`
  and `SDK Message Processing Step` (role editor, **Customization** tab). System Customizer alone
  is not enough, and the import fails with
  `is missing prvCreatePluginAssembly privilege ... for entity 'pluginassembly'`.
- **Copilot Studio provisioned in the target environment.** The solution declares a dependency on
  the `PowerVirtualAgents` managed solution, version `2026.6.3.20581040` or above, because the step
  references the `bot` table. Import fails with an explicit missing-dependency error if the target
  is on an older version.

Dependencies, restored from NuGet:

- `Microsoft.CrmSdk.CoreAssemblies` 9.0.2.60
- `Newtonsoft.Json` 13.0.4

## Build

Dataverse requires plug-in assemblies to be **strong-named**, so generate a signing key first
(the repository does not ship one):

```bash
sn -k BotCreationGuard.snk
```

`sn.exe` comes with the Windows SDK / Visual Studio Developer Command Prompt. The `.csproj`
already references `BotCreationGuard.snk` via `SignAssembly`.

Then:

```bash
dotnet build -c Release
```

The assembly lands in `bin\Release\net462\BotCreationGuard.dll`.

## Environment variable

Create an Environment Variable in the solution that carries the plug-in:

| Field | Value |
|-------|-------|
| Display name | `RestrictedRoleName` |
| Schema name | `nadabr_RestrictedRoleName` |
| Data type | Text |
| Default value | the exact name of the restricted security role |

The plug-in reads the **current value** first and falls back to the **default value**. Putting the
role name in the default value is what makes the shipped solution work on import without any
manual step, because default values travel with a solution and current values do not.

Set a **current value** only when the role is named differently in that particular environment.

> **Publisher prefix.** The published build uses the `nadabr_` prefix. If your solution uses
> another one (`new_`, `cr625_`, …), the schema name will differ, and you must update the
> `EnvironmentVariableSchemaName` constant in `BotCreationGuardPlugin.cs` to match, then rebuild
> and update the registered assembly. The prefix is baked into the compiled assembly; it is the
> one thing this project cannot resolve at runtime.
>
> Importing the solution from [Releases](../../releases) sidesteps this entirely: the solution
> brings its own `nadabr_RestrictedRoleName` definition, already matching the assembly.

## Registering the step

Open the Plugin Registration Tool:

```bash
pac tool prt
```

Connect to the environment, **Register New Assembly**, select
`bin\Release\net462\BotCreationGuard.dll`, then register a step on the plug-in with:

| Setting | Value |
|---------|-------|
| Message | `Create` |
| Primary entity | `bot` |
| Event pipeline stage | `Pre-operation` (stage 20) |
| Execution mode | Synchronous |
| Isolation mode | Sandbox |
| Deployment | Server |

`bot` does not appear in the tool's Primary Entity picker — it is a system table hidden from the
usual query surfaces — but typing it in by hand is accepted. Plug-in registration works at the SDK
metadata level, not through the UI-level filtering that hides `bot` from FetchXML Builder, the
Power Automate Dataverse connector and classic workflows.

To ship a new build later, right-click the assembly → **Update** and pick the new `.dll`. The
registered step does not need to be recreated — plug-in types are matched by full type name, so
the step and its configuration survive.

The assembly version stays `1.0.0.0` across builds, so the version number tells you nothing about
which binary is deployed. Check `modifiedon` instead:

```
/api/data/v9.2/pluginassemblies?$select=name,version,modifiedon&$filter=name eq 'BotCreationGuard'
```

## Using the solution instead

If you would rather not build anything, download the solution `.zip` from the
[Releases](../../releases) page and import it.

The solution is **unmanaged**. Importing it deposits unmanaged customisations in the target
environment, which do not uninstall cleanly by deleting the solution afterwards. That is a
deliberate choice — it lets you adapt the role, the step, or the variable to your tenant — but it
is worth knowing before you import into production.

It carries four components:

| Component | Type |
|---|---|
| `System Customizer - No GitHub Harness Agents` | Security role |
| `BotCreationGuard` | Plug-in assembly |
| `BotCreationGuard.BotCreationGuardPlugin: Create of bot` | SDK message processing step |
| `nadabr_RestrictedRoleName` | Environment variable |

At import, under **Advanced settings**, leave **Enable Plugin steps and flows included in the
solution** checked. Unchecked, the step arrives disabled and blocks nothing.

The environment variable arrives with its default value already set, so the guard is armed as soon
as the import completes. Assign the security role to the users you want to restrict, and you are
done.

### Building the solution yourself

If you assemble the solution rather than downloading it, note that **adding the plug-in assembly
does not add its steps**. They are separate components and must both be added explicitly:
**+ Add existing** → **More** → **Developer** → **Plug-in assembly**, then again for **Plug-in
step**. An assembly without its step imports without error and blocks nothing.

## Verifying it actually works

Do not assume the guard is active because the import succeeded. Every failure mode is silent.

1. In the target environment, open the legacy settings (**Resources** → **All legacy settings**),
   then **Settings** → **Administration** → **System Settings** → **Customization** tab, and set
   *Enable logging to plug-in trace log* to **All**.

   **All**, not *Exception*: in the failure cases the plug-in returns normally rather than
   throwing, so *Exception* logs nothing and you would wrongly conclude the plug-in is absent.

2. Assign the restricted role to a test user — your own account works, System Administrator is not
   exempt.

3. Create a harness agent as that user. You should get a red error carrying the plug-in's message,
   with the role name interpolated into it.

4. Whatever happens, open **Settings** → **Plug-in Trace Log** (or **Tables** → **Plug-in Trace
   Log** → **Data**) and look for a row whose Type Name is
   `BotCreationGuard.BotCreationGuardPlugin`.

Reading the result:

| Observation | Meaning |
|---|---|
| Row present, creation blocked | Working as intended |
| Row present, creation allowed | The plug-in ran and bailed out. Its **Message Block** column names the exact reason — template mismatch, recognizer mismatch, role not found, environment variable unreadable |
| **No row at all** | The step never executed — see below |

If no row appears, first confirm the log covers the moment of the attempt. Trace records are
timestamped, and exporting a log that predates your test is an easy mistake to make. Then check
that the step is registered and enabled in *that* environment:

```
/api/data/v9.2/sdkmessageprocessingsteps?$filter=contains(name,'BotCreationGuard')&$select=name,stage,mode,statecode&$expand=sdkmessagefilterid($select=primaryobjecttypecode)
```

Expect `stage: 20`, `mode: 0`, `statecode: 0` and `primaryobjecttypecode: "bot"`.

Re-run this verification after platform updates, not only after the first import. The signal the
plug-in keys on is undocumented and can change underneath you.

## Caveats

- The plug-in keys on `template` = `cliagent-1.0.0` and `recognizer.$kind` =
  `CLICopilotRecognizer`. These are undocumented implementation details of the harness; Microsoft
  can change them at any time, which would silently stop the blocking. Re-check them after
  platform updates.
- The role check matches on the role **name**, exactly. A rename in Dataverse, a trailing space,
  or a missing character breaks the match, and it fails permissively and silently.
- The role lookup runs against the initiating user (`context.InitiatingUserId`), which is the
  right identity for impersonated or flow-triggered creations.
- The guard only protects the environments it is installed in. A maker who can create agents in a
  personal or developer environment is unaffected. Tenant-level environment-creation governance is
  a separate concern.
- This is a restriction, not a security boundary: a user who can modify plug-in steps or solutions
  can disable it.

## Licence

MIT.
