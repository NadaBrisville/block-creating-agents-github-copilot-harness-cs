# Step by step installation

This guide is for anyone who wants the control running without building anything. No Visual
Studio, no Plugin Registration Tool, no code. Download a solution, import it, assign a role.

Once it is in place, any user holding the restricted security role is blocked from creating
Copilot Studio agents built on the CLI / GitHub Copilot harness. Everything else they do, including
creating ordinary agents, carries on untouched.

Count about ten minutes.

If you would rather understand how this works, or build it from source, read the
[README](README.md) instead.

---

## Before you start

**You need privileges to register plug-ins in the target environment.** System Administrator covers
it. Otherwise the account doing the import needs **Create** on `Plug-in Assembly`, `Plug-in Type`
and `SDK Message Processing Step`. System Customizer on its own is not enough, and the import fails
with `is missing prvCreatePluginAssembly privilege`.

**Copilot Studio must be provisioned in the target environment.** The solution declares a
dependency on the `PowerVirtualAgents` managed solution, because the plug-in step references the
`bot` table. Importing into an environment without Copilot Studio fails with an explicit
missing-dependency error.

**The solution is unmanaged.** That is deliberate, so you can adapt the role, the step or the
variable afterwards. The trade-off is that unmanaged customisations do not uninstall cleanly by
deleting the solution later.

---

## Step 1. Download the solution

Go to the [Releases](../../releases) page of this repository and download the `.zip` asset from the
latest release. Do not unzip it.

![The Releases page with the solution zip attached](docs/images/01-releases-page.png)

> **[SCREENSHOT NEEDED: `01-releases-page.png`]**
> The repository Releases page, showing the latest release with the `.zip` under **Assets**.
> Frame the release title, the version tag and the asset filename together.

---

## Step 2. Import it into the target environment

Open [make.powerapps.com](https://make.powerapps.com) and switch to the environment you want to
protect, using the environment picker at the top right. Getting this wrong is the most common
mistake, so check the name before going further.

### 2.1 Start the import

Go to **Solutions**, then **Import solution** in the command bar.

![Solutions area with the Import solution command](docs/images/02-import-solution-button.png)

> **[SCREENSHOT NEEDED: `02-import-solution-button.png`]**
> The **Solutions** list with the command bar visible and **Import solution** readable. Include the
> environment name at the top right if it fits, so readers see where they are.

### 2.2 Select the file

Choose **Browse**, pick the `.zip` you downloaded, then **Next**.

![Browsing to the downloaded solution zip](docs/images/03-browse-zip.png)

> **[SCREENSHOT NEEDED: `03-browse-zip.png`]**
> The import panel at the file-selection stage, with the chosen `.zip` filename visible.

### 2.3 Check the details, and one checkbox

The panel shows the solution name, its type and its version. Expand **Advanced settings** and make
sure **Enable Plugin steps and flows included in the solution** is **checked**.

**This checkbox matters more than anything else on this screen.** Left unchecked, the import
succeeds, every component lands correctly, and the guard blocks nothing at all, with no error
anywhere to tell you.

![Import details with Advanced settings expanded and the checkbox ticked](docs/images/04-import-advanced-settings.png)

> **[SCREENSHOT NEEDED: `04-import-advanced-settings.png`]**
> The import panel showing solution name, **Type: Unmanaged**, the version number, and
> **Advanced settings** expanded with the checkbox ticked. One image covering all four.

### 2.4 Confirm the environment variable

The wizard asks you to confirm the environment variables carried by the solution. The
`RestrictedRoleName` variable arrives with its default value already filled in with the name of the
security role.

Leave it as it is. The value only needs changing if you intend to use a role that already exists in
your environment under a different name, which is covered at the end of this guide.

![The environment variables step with RestrictedRoleName prefilled](docs/images/05-environment-variable-step.png)

> **[SCREENSHOT NEEDED: `05-environment-variable-step.png`]**
> The **Environment Variables** step of the import wizard, with `RestrictedRoleName` and its
> prefilled value fully visible. Widen the panel so the whole role name is readable, right through
> to the final "s" of "Agents".

### 2.5 Import

Select **Import** and wait. It usually takes a couple of minutes.

![The success banner after import](docs/images/06-import-success.png)

> **[SCREENSHOT NEEDED: `06-import-success.png`]**
> The green confirmation banner reading that the solution imported successfully.

### 2.6 Confirm what landed

Open the imported solution and go to **Objects**. Four components must be listed:

| Component | Type |
|---|---|
| `System Customizer - No GitHub Harness Agents` | Security role |
| `BotCreationGuard` | Plug-in assembly |
| `BotCreationGuard.BotCreationGuardPlugin: Create of bot` | Plug-in step |
| `nadabr_RestrictedRoleName` | Environment variable |

The **Plug-in step** row is the one to look for. An assembly without its step imports without a
single error and blocks nothing.

![The solution Objects list showing the four components](docs/images/07-solution-objects.png)

> **[SCREENSHOT NEEDED: `07-solution-objects.png`]**
> The solution's **Objects → All** view, with the four rows and the **Type** column visible.

---

## Step 3. Assign the role

Nothing is blocked until someone actually holds the restricted role. Assigning it is what puts a
user inside the restriction.

Go to the [Power Platform admin center](https://admin.powerplatform.microsoft.com), open your
environment, then **Settings → Users + permissions → Users**. Pick a user, choose **Manage security
roles**, tick **System Customizer - No GitHub Harness Agents**, and save.

A few things worth knowing:

- **System Administrator does not exempt anyone.** The plug-in checks whether the restricted role
  is present, not what else the user can do. An admin who also carries this role is blocked like
  everybody else, which makes your own account a perfectly good test subject.
- **Roles inherited from a team count too.** If you assign the role to an Entra group team rather
  than to individuals, its members are covered.
- **The role is a copy of System Customizer.** Users receiving it keep the customization rights
  they need, and can still create ordinary agents.

![Assigning the security role to a user](docs/images/08-assign-role.png)

> **[SCREENSHOT NEEDED: `08-assign-role.png`]**
> Either the **Manage security roles** panel with the role ticked, or the user summary showing
> `System Customizer - No GitHub Harness Agents` under **Direct Assigned Roles**. The second is
> clearer, since it proves the assignment took effect.

---

## Step 4. Check that it actually works

Do not skip this. Every failure mode of this plug-in is silent and permissive: a misconfiguration
lets creations through rather than locking your environment out, which means a broken installation
and a working one look identical from the outside.

Sign in as a user holding the role, or assign it to yourself, and try to create an agent through
the CLI / GitHub Copilot harness in Copilot Studio. You should get a red panel carrying the
plug-in's message, with the role name quoted inside it.

![Copilot Studio rejecting the creation](docs/images/09-blocked-creation.png)

> **[SCREENSHOT NEEDED: `09-blocked-creation.png`]**
> The red **"We couldn't create the agent"** panel, with enough of the message underneath readable
> to see the role name in quotes.

Then create an ordinary agent with the same user. It should go through without any interference.
The restriction stays scoped to the single signal it was built to catch.

If the creation is **not** blocked, see the troubleshooting section below.

---

## Optional. Using a role that already exists in your environment

The solution ships its own security role, and most people should simply use it.

If you would rather point the guard at a role you already have, you do not need to rebuild
anything. Open the solution, select the `RestrictedRoleName` environment variable, and set a
**current value** to the exact name of your role. The plug-in reads the current value first and
falls back to the default value, so the current value wins.

Two warnings:

- The comparison is an **exact match on the role name**. A trailing space or a missing character
  makes the lookup fail, and it fails permissively, with no error.
- Set a **current value**, not a new default value. Current values are specific to an environment
  and do not travel with the solution.

---

## Troubleshooting

**The import failed with `is missing prvCreatePluginAssembly privilege`.**
The account doing the import lacks plug-in registration privileges. Import with a System
Administrator account, or grant Create on `Plug-in Assembly`, `Plug-in Type` and
`SDK Message Processing Step` to the role in question.

**The import failed on a missing dependency.**
Copilot Studio is not provisioned in the target environment, or it runs an older
`PowerVirtualAgents` version than the one the solution was exported against.

**Everything imported, but nothing is blocked.**
Work through these in order:

1. **Is the Plug-in step in the solution?** Open **Objects** and check the four rows from step 2.6.
   A missing step is the most frequent cause.
2. **Was the Advanced settings checkbox ticked at import?** If not, the step arrived disabled.
   Re-import with the box ticked.
3. **Does the user actually hold the role?** Check under **Direct Assigned Roles**, or through
   their team membership.
4. **Does the environment variable value match the role name exactly?** Compare them character by
   character, including the final "s".

If all four check out, turn on plug-in trace logging and read what the plug-in itself reports. The
[README](README.md#verifying-it-actually-works) has the full procedure, and the trace names the
exact reason it let the creation through.

---

## What this does not cover

This control only protects the environments you install it in. A maker who can create a personal or
developer environment falls outside its reach entirely.

It also does not replace credit hygiene. In any environment with no business consuming credits,
allocate zero credits and turn off the tenant pool draw. That stops the same problem at the source
and takes one setting.

Licence: MIT.
