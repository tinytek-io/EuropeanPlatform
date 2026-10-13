# 👋 Welcome to EuropeanPlatform

Kick-start building top-tier B2B & B2C cloud SaaS products with sleek design, fully localized and accessible, vertical slice architecture, automated and fast DevOps, and top-notch security.

Ships with signup and login via Google or email one-time password, Stripe-powered subscription and payment management with plan upgrades, downgrades, and invoicing, feature flags with A/B-rollout, plan-gating, and per-user/tenant overrides, and a back-office dashboard with MRR and revenue trends, plan distribution, and tenant growth.

Built to demonstrate seamless flow: backend contracts feed a fully-typed React UI, pipelines make fully automated deployments to Scaleway, and a multi-agent workflow built on Claude Code's native [Agent Teams](https://code.claude.com/docs/en/agent-teams) where EuropeanPlatform-expert agents collaborate to deliver complete features following the opinionated architecture. Think of it as a ready-made blueprint, not a pile of parts to assemble.

## What's inside

* **Backend** - .NET 10 and C# 14 following the principles of vertical slice architecture, DDD, CQRS, and clean code
* **Frontend** - React 19, TypeScript, TanStack Router & Query, ShadCN 2.0 with Base UI for accessible UI
* **Deployment** - Infrastructure as Code with Terraform, and application deployments through the Aspire publish/deploy pipeline to a Kubernetes cluster
* **Infrastructure** - Cost efficient and scalable Scaleway services like Kapsule (Kubernetes), Managed PostgreSQL, and Object Storage
* **Developer CLI** - Extendable .NET CLI for DevEx - set up CI/CD is one command and a couple of questions
* **AI rules** - 30+ rules & skills, refined and battle-tested over a year of daily use, capturing our opinionated patterns
* **Multi-agent development** - Agent Teams workflow where specialized Claude Code agents with deep EuropeanPlatform expertise collaborate end-to-end

Follow our [up-to-date roadmap](https://github.com/orgs/PlatformPlatform/projects/2/views/2).

Show your support for our project - give us a star on GitHub! It truly means a lot! ⭐

### Back office

Operate the platform from a dedicated SPA on its own hostname, behind its own staff sign-in (a standard OIDC provider, with a mock identity selector as the stand-in in development and staging):

* **Dashboard** - KPI tiles for total accounts, blended MRR, all-time revenue, active users, and live sessions; trend cards for MRR, revenue, tenant growth, plan distribution, user logins; activity feeds for recent signups, payments, logins, and Stripe webhook events
* **Accounts** - Search and filter every tenant; drill into detail with owner, plan, and signup activity
* **Users** - Cross-tenant user list with role and last-seen filters; drill into per-user profile and tenant
* **Invoices** - Paginated invoice ledger across every account with Stripe drift detection so finance can reconcile what's in Stripe vs. what landed in the database
* **Billing events** - Authoritative event log of subscription, payment, and billing transitions, filterable by event type and account, with deep-link from dashboard cards
* **Feature flags** - Four levers (System / Subscription plan / Account / User) plus A/B-rollout with rich telemetry; declared in C# and surfaced as a strongly-typed React hook; back-office UI for rollouts and overrides

<img src="https://platformplatformgithub.blob.core.windows.net/BackOffice.gif" alt="EuropeanPlatform Back Office" title="EuropeanPlatform Back Office" />

### User-facing SaaS product

Production-ready end-user surfaces — fully localized, accessible, and ready to brand as your own product:

* **Signup** - Tenant signup with email one-time password or Google OAuth (OpenID Connect with PKCE)
* **Login** - Same OTP and Google sign-in flows, with `UNLOCK` shortcut on localhost so dev mail is optional
* **Welcome** - First-run guided flow for naming the account, uploading a logo, and inviting colleagues
* **Account settings** - Owner-editable account name, logo, and danger-zone account deletion
* **User management** - Invite users, change roles (Owner/Admin/Member), bulk delete, and recycle-bin restore
* **Subscription & billing** - Embedded Stripe Checkout & Payment Element, prorated upgrades/downgrades, billing-info editing, scheduled-downgrade banner, dunning, and payment history with invoices and credit notes
* **User profile** - Personal profile with avatar upload (Gravatar fallback), first/last name, email, and job title
* **User preferences** - Theme (light/dark/system) and zoom per device, language per profile
* **Sessions** - Active session list with device type, browser, and OS, plus one-click revocation of any session

<img src="https://platformplatformgithub.blob.core.windows.net/$root/EuropeanPlatformDemo.gif" alt="EuropeanPlatform Demo" title="EuropeanPlatform Demo" />

# Getting Started

TL;DR: Requires .NET 10, Node, and Docker. Clone the repo and `dotnet run` from `developer-cli/` to start Aspire.

### Prerequisites

For development, you need .NET, Docker, and Node. And the Scaleway CLI (`scw`), Terraform, and the Aspire CLI for setting up deployments.

<details>

<summary>Install prerequisites for Windows</summary>
	
1.	Open a PowerShell terminal as Administrator and run the following command to install Windows Subsystem for Linux (required for Docker). Restart your computer if prompted.
  
    ```powershell
    wsl --install
    ```

2.	From an Administrator PowerShell terminal, use [winget](https://learn.microsoft.com/en-us/windows/package-manager/winget/) (preinstalled on Windows 11) to install any missing packages:

    ```powershell
    winget install Microsoft.DotNet.SDK.10
    winget install Git.Git
    winget install Docker.DockerDesktop
    winget install Scaleway.ScalewayCli
    winget install GitHub.cli
    ```

3. Install Node.js — the version must match [`.node-version`](./application/.node-version). We recommend [fnm](https://github.com/Schniz/fnm) which auto-installs the exact version via the Developer CLI. When using an IDE like Rider, ensure the active fnm version matches [`.node-version`](./application/.node-version).

    ```powershell
    # Option A: fnm (recommended)
    winget install Schniz.fnm

    # Option B: Node.js directly
    winget install OpenJS.NodeJS
    ```

4.	(Recommended) Install the [Aspire CLI](https://aspire.dev/get-started/install-cli/) — provides the [Aspire MCP server](https://aspire.dev/get-started/aspire-mcp-server/) so Claude Code can inspect AppHost resources, logs, and traces:

    ```powershell
    irm https://aspire.dev/install.ps1 | iex
    ```

    Verify with `aspire --version`. Restart your terminal if the command is not yet on PATH.

5.	(Recommended) Install language servers for enhanced Claude Code support:

    ```powershell
    npm install -g typescript-language-server typescript
    dotnet tool install -g csharp-ls
    ```

</details>

<details>

<summary>Install prerequisites for Mac</summary>

Open a terminal and run the following commands (if not installed):

1. Install [Homebrew](https://brew.sh/), a package manager for Mac

2. Install packages

   ```bash
   brew install --cask dotnet-sdk
   brew install --cask docker
   brew install git gh scaleway/tap/scw terraform
   ```

3. Install Node.js — the version must match [`.node-version`](./application/.node-version). We recommend [fnm](https://github.com/Schniz/fnm) which auto-installs the exact version via the Developer CLI. When using an IDE like Rider, ensure the active fnm version matches [`.node-version`](./application/.node-version).

   ```bash
   # Option A: fnm (recommended)
   brew install fnm

   # Option B: Node.js directly
   brew install node
   ```

4. (Recommended) Install the [Aspire CLI](https://aspire.dev/get-started/install-cli/) — provides the [Aspire MCP server](https://aspire.dev/get-started/aspire-mcp-server/) so Claude Code can inspect AppHost resources, logs, and traces:

   ```bash
   curl -sSL https://aspire.dev/install.sh | bash
   ```

   Verify with `aspire --version`. Restart your terminal if the command is not yet on PATH.

5. (Recommended) Install language servers for enhanced Claude Code support:

   ```bash
   npm install -g typescript-language-server typescript
   dotnet tool install -g csharp-ls
   ````

</details>

<details>

<summary>Install prerequisites for Linux (Ubuntu/Debian)</summary>

Open a terminal and run the following commands (if not installed):

1. Install basic tools

   ```bash
   sudo apt update && sudo apt install -y git wget curl libnss3-tools
   ```

2. Add Microsoft package repository

   ```bash
   source /etc/os-release
   wget https://packages.microsoft.com/config/$ID/$VERSION_ID/packages-microsoft-prod.deb -O packages-microsoft-prod.deb
   sudo dpkg -i packages-microsoft-prod.deb
   rm packages-microsoft-prod.deb
   ```

3. Install .NET SDK and Docker

   ```bash
   sudo apt-get update && sudo apt-get install -y dotnet-sdk-10.0 docker.io docker-compose-v2
   ```

   ```bash
   sudo systemctl enable --now docker
   sudo usermod -aG docker $USER
   ```

4. Install Node.js — the version must match [`.node-version`](./application/.node-version). We recommend [fnm](https://github.com/Schniz/fnm) which auto-installs the exact version via the Developer CLI. When using an IDE like Rider, ensure the active fnm version matches [`.node-version`](./application/.node-version).

   ```bash
   # Option A: fnm (recommended)
   curl -fsSL https://fnm.vercel.app/install | bash

   # Option B: Node.js directly
   curl -fsSL https://deb.nodesource.com/setup_24.x | sudo -E bash -
   sudo apt-get install -y nodejs
   ```

5. (Recommended) Install the [Aspire CLI](https://aspire.dev/get-started/install-cli/) — provides the [Aspire MCP server](https://aspire.dev/get-started/aspire-mcp-server/) so Claude Code can inspect AppHost resources, logs, and traces

   ```bash
   curl -sSL https://aspire.dev/install.sh | bash
   ```

   Verify with `aspire --version`. Restart your terminal if the command is not yet on PATH.

6. Trust the HTTPS development certificate

   ```bash
   echo 'export SSL_CERT_DIR="$HOME/.aspnet/dev-certs/trust:${SSL_CERT_DIR:-/usr/lib/ssl/certs}"' >> ~/.bashrc
   ```

   ```bash
   source ~/.bashrc
   ```

   ```bash
   dotnet dev-certs https --trust
   ```

7. **Log out and log back in** to apply Docker group and shell configuration changes.

8. (Recommended) Install language servers for enhanced Claude Code support

   ```bash
   npm install -g typescript-language-server typescript
   dotnet tool install -g csharp-ls
   ```

9. (Optional) If using Snap Chromium, trust the certificate in its sandbox

   ```bash
   certutil -d sql:$HOME/snap/chromium/current/.pki/nssdb -L >/dev/null 2>&1 || (mkdir -p $HOME/snap/chromium/current/.pki/nssdb && certutil -d sql:$HOME/snap/chromium/current/.pki/nssdb -N --empty-password)
   dotnet dev-certs https --trust
   ```

10. (Optional) Install the GitHub CLI and Scaleway CLI (needed for deployment setup)

   ```bash
   (type -p wget >/dev/null || (sudo apt update && sudo apt-get install wget -y)) \
   && sudo mkdir -p -m 755 /etc/apt/keyrings \
   && wget -qO- https://cli.github.com/packages/githubcli-archive-keyring.gpg | sudo tee /etc/apt/keyrings/githubcli-archive-keyring.gpg > /dev/null \
   && sudo chmod go+r /etc/apt/keyrings/githubcli-archive-keyring.gpg \
   && echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/githubcli-archive-keyring.gpg] https://cli.github.com/packages stable main" | sudo tee /etc/apt/sources.list.d/github-cli.list > /dev/null
   sudo apt-get update && sudo apt-get install -y gh
   curl -o scw https://github.com/scaleway/scaleway-cli/releases/latest/download/scw-linux-x86_64 && sudo mv scw /usr/local/bin/scw
   ```

</details>

## 1. Clone the repository

```bash
git clone https://github.com/platformplatform/PlatformPlatform.git
```

We recommend you keep the commit history, which serves as a great learning and troubleshooting resource. 😃

## 2. (Optional) Install the Developer CLI

The EuropeanPlatform CLI provides convenient commands for common tasks. From the cloned repository, install it globally to use the `pp` command from anywhere in your terminal.

```bash
cd developer-cli
dotnet run install
```

Restart your terminal to make the `pp` command available.

![Developer CLI](https://platformplatformgithub.blob.core.windows.net/$root/developer-cli.png)

## 3. Run the Aspire AppHost to spin up everything on localhost

Using Aspire, docker images with PostgreSQL, Blob Storage emulator, and development mail server will be downloaded and started. No need to install anything, or learn complicated commands. Simply run this command, and everything just works 🎉

With the CLI installed:

```bash
pp run # First time downloading Docker containers will take several minutes
```

Pass an optional base port (e.g. `pp run 11000`) to run a parallel stack from another worktree.

Or without the CLI:

```bash
cd application/AppHost
dotnet run
```

On first startup, Aspire will prompt for `stripe-enabled` -- enter `true` to configure Stripe integration (see the optional Stripe setup section below) or `false` to skip.

Once the Aspire dashboard fully loads, click to the WebApp and sign up for a new account (https://app.dev.localhost:9000/signup). A one-time password (OTP) will be sent to the development mail server, but for local development, you can always use the code `UNLOCK` instead of checking the mail server.

### 3.1 (Optional) Set up Google OAuth for "Sign in with Google" on localhost

EuropeanPlatform supports authentication via Google OAuth using OpenID Connect with PKCE. This is optional for local development since email-based one-time passwords work without any configuration. The Aspire dashboard prompts whether to enable Google OAuth on first startup.

<details>

<summary>Google Cloud Console setup</summary>

1. Go to [Google Cloud Console](https://console.cloud.google.com/)
2. Create a new project (e.g., "YourProduct OAuth")
3. Navigate to **APIs & Services** > **Credentials**
4. Configure OAuth consent screen (first time only):
   - App name, support email, audience (External), contact info
   - Agree to Google API Services: User Data Policy
5. Create OAuth client ID:
   - Application type: "Web application"
   - Name: "YourProduct Localhost"
6. Add Authorized redirect URIs (Google does not allow https://app.dev.localhost:9000/):
   - `https://localhost:9000/api/account/authentication/Google/login/callback`
   - `https://localhost:9000/api/account/authentication/Google/signup/callback`
7. Note the Client ID and Client Secret

</details>

**Aspire parameter configuration** (two restarts required):

1. **First restart**: Aspire prompts whether to enable Google OAuth. Enter `true` to enable or `false` to skip. Once entered, restart Aspire.
2. **Second restart**: Aspire prompts for the **Client ID** and **Client Secret**. Enter the values from the Google Cloud Console, then restart Aspire to apply the configuration.

All values are stored securely in .NET user secrets and persist across restarts.

### 3.2 (Optional) Set up Stripe sandbox for localhost

EuropeanPlatform includes a comprehensive Stripe integration for subscription management and payments: embedded Stripe checkout and payment elements, prorated plan upgrades and downgrades, tax management, localized UI and invoice text, refund overview in billing history, telemetry events for the subscription lifecycle, grandfather pricing for existing subscribers, dunning and failed payment recovery, and full sync between Stripe and the local database via webhooks. The Stripe Dashboard must be set up and configured according to the Stripe Dashboard setup guide below before enabling Stripe in Aspire.

<details>

<summary>Stripe Dashboard setup</summary>

Each developer needs their own Stripe sandbox. The local database stays in sync with Stripe via webhook events -- when payments, subscriptions, or billing details change in Stripe, webhook events update the local database with matching customer and subscription IDs. If developers share a sandbox, webhook events from one developer's actions would corrupt another developer's local database.

1. **Create a sandbox**: Go to [Stripe Dashboard](https://dashboard.stripe.com) and create an account. Click the **account picker** (top-left) > **Sandboxes** > **Create**. Name it `dev-yourname` and open it. All subsequent steps are performed inside your sandbox.
2. **Create products**: Navigate to **Product catalog** > **+ Create product**. Create a `Standard` product with **Recurring** / **Monthly** pricing (e.g., 19 EUR), then a `Premium` product (e.g., 39 EUR). Important: Click **More pricing options** and add `standard_monthly` and `premium_monthly` respectively in the **Lookup key** field.
3. **Disable non-card payment methods**: Go to **Settings** (gear icon) > **Payments** > **Payment methods**. Turn off every payment method except **Cards** (and **Cartes Bancaires** which cannot be disabled)
4. **Limit to 1 subscription**: Go to **Settings** > **Payments** > **Checkout and Payment Links**, scroll to **Subscriptions**, and enable **Limit customers to 1 subscription**. Add link to `https://app.dev.localhost:9000/account/billing`
5. **Configure failed payment recovery**: Go to **Settings** > **Billing** > **Subscriptions and emails** > **Manage failed payments**, and configure desired retry behavior
6. **Configure email notifications**: Go to **Settings** > **Billing** > **Subscriptions and emails** > **Email notifications and customer management**, and enable all settings as you see fit. Set "Use your own custom link" to `https://app.dev.localhost:9000/account/billing`
7. **Enable 3D Secure**: Go to **Settings** > **Billing** > **Subscriptions and emails** > **Manage payments that require confirmation**, and check off **Enable 3D Secure**. The embedded Stripe components support showing e.g. Visa and Danish MitID multi-factor confirmation dialogs
8. **Set invoice prefix**: Go to **Settings** > **Billing** > **Invoices**. In the **Invoice numbering** section, change the **Invoice prefix** to something meaningful for your organization, and optionally reset the **Invoice sequence** if needed
9. **Disable payment link in invoice emails**: Go to **Settings** > **Billing** > **Invoices** and uncheck **Include a link to a payment page in the invoice email**
10. **Set up Tax**: Go to **More** > **Tax** > **Locations** > **+ Add test registration** and follow the guide. Below is an example for a Danish company (adapt based on your business):
    - Fill out the "Add head office address" side pane with your company address. Click **Continue**
    - In "Add Tax registration" select your country (e.g., **Denmark**) and **I'm already registered**. Click **Continue**
    - Select **Domestic** (e.g., "registered in Denmark"). Click **Continue**
    - Select **No** to "Do you want to collect VAT on cross-border sales...". Click **Continue**
    - Select **Yes** to "Have your sales of goods or digital services... been less than EUR 10,000". Click **Continue**
    - Select **Start collecting immediately** in "Schedule tax collection". Click **Continue**
    - Select **Software as a service (SaaS) - business use** in "Confirm your tax rates" (should show 25% for Denmark). Click **Start collecting**
11. **Get API keys**: Navigate to **Developers** > **API keys**. Note the **Publishable key** (`pk_test_...`) and **Secret key** (`sk_test_...`). These will be needed for the Aspire configuration below.

</details>

**Aspire parameter configuration** (two restarts required):

1. **First restart**: Aspire prompts for the **Publishable key** and **Secret key** (API key). Once entered, the Stripe CLI container starts and connects to Stripe. Find the generated webhook signing secret in either:
   - The **Stripe Dashboard** under **Developers** > **Workbench** > **Webhooks** -- click the three-dot menu on the event destination to reveal the signing secret
   - The **stripe-cli** container logs in the Aspire dashboard (look for "Your webhook signing secret is whsec_...")
2. **Second restart**: Aspire prompts for the **Webhook secret**. Enter the `whsec_` value from the previous step.

All values are stored securely in .NET user secrets and persist across restarts.

## 4. Deploy to Scaleway (Terraform + Aspire deploy)

Deployment is fully scripted and reproducible from a developer machine. Infrastructure lives in
Terraform (`cloud-infrastructure/scaleway`), applications deploy through the Aspire
publish/deploy pipeline. After the two Terraform modules are applied (see
[cloud-infrastructure/README.md](./cloud-infrastructure/README.md) for credentials and
variables), one command deploys an environment:

```bash
dotnet run --project developer-cli -- deploy -e staging
```

The command verifies the infrastructure matches the Terraform configuration (refusing to deploy
over drift), resolves the cluster kubeconfig and the per-workload deploy keys from Terraform
outputs, and runs the Aspire build/publish/deploy pipeline with live progress. Afterwards, run
`edge-health` for a read-only health check of the edge and the public endpoints.

Workloads push OpenTelemetry metrics, logs, and traces to the project's Cockpit. For a new
environment, open the Cockpit Grafana once in the Scaleway console (the project's
`...dashboard.cockpit.scaleway.com` URL) — Grafana registration happens on that first visit.
Until then the Terraform-managed data sources push telemetry fine but do not appear in Grafana,
and applies that change a data source report a harmless data-source sync error.

After the first deploy, create the DNS records for your domain (one CNAME per public hostname
pointing at its Edge Services pipeline, plus the transactional email records). The
[DNS guide](./cloud-infrastructure/scaleway/docs/dns-guide.md) lists the exact record set and
verification steps.

Secrets are provisioned in Scaleway Secret Manager by Terraform (connection strings, signing
keys, API keys), and each workload runs with its own IAM application that can read only its
secrets. There are no secrets in the repository, in images, or in the Helm chart.

The infrastructure is configured with hosting costs in focus: a staging environment with a
small Kapsule node pool runs for a few tens of euros per month.

### (Optional) Configure Google OAuth for staging and production

If you set up Google OAuth locally, store the credentials for staging and production as Secret Manager entries per the environment module (they surface to workloads as configuration keys):

Remember to add redirect URIs for each environment in your Google Cloud Console configuration, e.g.:
- `https://staging.yourproduct.com/api/account/authentication/Google/login/callback`
- `https://staging.yourproduct.com/api/account/authentication/Google/signup/callback`
- `https://app.yourproduct.com/api/account/authentication/Google/login/callback`
- `https://app.yourproduct.com/api/account/authentication/Google/signup/callback`

### (Optional) Configure a staff OIDC provider for the back-office

The back-office staff sign-in uses any standard OpenID Connect provider (Entra ID, Google
Workspace, Auth0, ...) when one is configured; otherwise environments fall back to the mock
identity selector (local development, and staging until a provider exists).

To wire a provider, set — as Secret Manager entries for the back-office workloads or environment
variables on the back-office API — the keys under `BackOffice:Oidc`:

- `BackOffice:Oidc:Authority` — the provider's base URL with a discoverable
  `.well-known/openid-configuration` (e.g. `https://login.microsoftonline.com/{tenant}/v2.0`)
- `BackOffice:Oidc:ClientId` — the application/client ID registered with the provider
- `BackOffice:Oidc:ClientSecret` — the client secret (`BackOffice__Oidc__ClientSecret` as a
  Secret Manager entry)

Register the redirect URI `https://back-office.<your-domain>/.auth/login/aad/callback` with the
provider. Back-office admin actions additionally require `BackOffice:AdminsGroupId` set to the
value the provider issues in the `groups` claim for staff administrators — the back-office's own
admin policy and account's internal-API admin gate both read it. When these keys are
configured, the mock identity selector is disabled automatically.

#### Test the OIDC flow locally with the Authelia harness

The local dev stack can run [Authelia](https://www.authelia.com) — a small open-source OIDC
provider — as a stand-in for a production identity provider, exercising the exact same flow:
discovery, the authorization redirect, code + PKCE, the callback, and the `groups` claim that
gates admin features.

1. Start the dev stack with the harness enabled: `AUTHELIA__ENABLED=true dotnet run --project
   AppHost` (or export `Authelia__Enabled=true`). This adds `authelia` + `authelia-redis`
   containers at `https://authelia.dev.localhost:9091`, wires the back-office to them, and hides
   the mock login picker.
2. Run the harness spec against it:

   ```bash
   dotnet run --project developer-cli -- e2e back-office-oidc --no-wait-for-aspire
   ```

   with `BACK_OFFICE_OIDC_ENABLED=true` in the environment (the spec skips itself otherwise, so
   it stays out of the default suite). It signs in as `oidcadmin` / `OIDCTest123!` — a static
   dev user in the `admins` group (see `application/AppHost/Authelia/`) — and asserts the
   dashboard renders.

The harness certificate is self-signed (`authelia-tls.pem`), so the OIDC backchannel runs with
the dev-only `BackOffice:Oidc:DevAllowUntrustedCertificate` bypass — leave that unset for real
providers.

### (Optional) Configure Stripe for staging and production

Create a separate Stripe account (or sandbox) for each environment. For production, use a live Stripe account instead of a sandbox. Follow the Stripe Dashboard setup steps in [section 3.2](#32-optional-set-up-stripe-sandbox-for-localhost) to configure products, payment methods, tax, invoices, and other settings.

On localhost, the Stripe CLI container automatically forwards webhook events. On staging and production, you need to configure a webhook endpoint manually in the Stripe Dashboard:

1. Go to **Developers** > **Webhooks** > **Add destination**
2. Keep **Your account** selected. Under events, select the **Charge**, **Checkout**, **Credit Note**, **Customer**, **Invoice**, **Payment Intent**, **Payment Method**, **Price**, **Product**, **Refund**, **Setup Intent**, and **Subscription Schedule** categories (roughly 91 events as of March 2026, though Stripe may add or remove events over time). Do not use **Select all** as it includes v2 thin-payload events the handler does not support. Click **Continue**
3. Select **Webhook endpoint** as the destination type
4. Set the **Destination name** to the environment (e.g., `Staging` or `Production`), set the **Endpoint URL** to `https://app.yourproduct.com/api/account/subscriptions/stripe-webhook` (replace with your actual domain), and click **Continue**
5. On the destination detail page, click **Reveal** under **Signing secret** to get the webhook secret (`whsec_...`)

Provision the **Publishable Key**, **API Key** (Secret key), and **Webhook Secret** as Secret Manager entries for the environment (the same keys the local user-secrets setup uses). The subscription feature is enabled when all three are present.

### Back-office access

The back-office runs as its own self-contained system — a backend-for-frontend over the feature
systems' internal APIs — on its own hostname (`back-office.<domain>`), protected by an
authentication gate: on staging this is MockEasyAuth (a deliberate stand-in until a proper OIDC
provider for staff accounts is configured), and the edge pipelines serve its hostname with the
same TLS, WAF, and caching as the app.

To grant a person access once staff OIDC is configured, add them to the administrator group in
the identity provider; the platform-issued token then carries the claim the back-office uses to
enforce admin-only endpoints.

Add the back-office CNAME record the same way as the app hostname (see the
[DNS guide](./cloud-infrastructure/scaleway/docs/dns-guide.md)).

# Multi-Agent Development with Claude Code

EuropeanPlatform includes a multi-agent autonomous development workflow powered by [Claude Code Agent Teams](https://code.claude.com/docs/en/agent-teams). Specialized AI agents collaborate to deliver complete features, from requirements to production-ready code, while enforcing enterprise-grade quality standards.

The entire process can take several hours depending on complexity, but at the end you get a fully implemented feature: backend logic, database migrations, API endpoints, frontend UI, localization, and end-to-end tests. All committed. All tests passing. Ready to ship.

The agents work like a real engineering team, inside the tools you already use. Features and tasks live in your existing product management system (Linear, Azure DevOps, Jira, or markdown files on disk), not in a bespoke AI-only tracker. Write the feature and tasks yourself, or have the team lead interview you and create them for you. From there, the team lead delegates to engineers, who move tasks from planned to active when they start. Reviewers move them to review. The Guardian moves them to completed on a successful commit. You watch progress, comment on tasks, reprioritize, add bugs mid-flight, or restart tasks the agents got wrong, exactly the same way you would with a human team. The full audit trail lives in your product management tool alongside all your other work.

<img src="https://platformplatformgithub.blob.core.windows.net/$root/MultiAgentLinearWorkflow.gif" alt="Multi Agent Workflow Linear" title="Multi Agent Workflow Linear" />

## What makes this different

**Zero-tolerance code reviews**: AI agents follow rules well until they hit problems, then cut corners, which is why many struggle to get AI to write production-ready code. Dedicated reviewer agents catch this. They reject any code that can objectively be made better: compiler warnings, static analysis errors, browser console warnings, or deviation from established patterns. All warnings including warnings in seemingly unrelated parts of the system are fixed. This boy scout rule approach ensures every commit meets production standards.

**Native Agent Teams coordination**: Built on Claude Code's Agent Teams primitives: `TeamCreate`, `Task` with `team_name`, `SendMessage`, and a shared `TaskList`. The team lead spawns and coordinates all agents automatically.

**Real collaboration via `SendInterruptSignal`**: Native Agent Teams are fragile out of the box. Agents treat `SendMessage` like an inbox: they finish their current task fully before reading the next message, so a team lead that sends a nudge mid-flight never hears back. Team leads then assume the agent is stuck, spawn replacements, and the new agents behave the same way. EuropeanPlatform ships a custom MCP command, `SendInterruptSignal`, that solves this. It pairs with an ID-correlated follow-up `SendMessage` and is auto-delivered by a `PostToolUse` hook as a blocking error on the target agent's next tool call. Agents skip stale queued messages until they find the matching interrupt ID. Engineers interrupt QA when contracts change. Reviewers interrupt engineers with in-progress findings. The result is agents that truly collaborate in real time instead of talking past each other.

**Plan before acting**: The team lead always starts in plan mode. Before spawning any agents, it investigates the work, then presents a plan describing which agents will be spawned, what each will do, and the expected sequence. Implementation begins only once the user approves. Small tasks get brief plans, large tasks get detailed plans.

**Parallel execution with task sets**: Backend, frontend, and E2E tracks run concurrently within each task set. Engineers implement in parallel, reviewers validate independently, and the Guardian commits everything in dependency order once all tracks are approved.

**Guardian-owned commits**: A dedicated Guardian owns all git writes. Reviewers send one approval message per track with the full file list; the Guardian stages it atomically. Once every track is staged, the Guardian runs the pre-commit pipeline (build, test, format, lint, Aspire restart, smoke tests) and commits in dependency order. No other agent touches git.

**Explicit task status ownership**: Every status transition has exactly one owner. Engineers move tasks from planned to active when starting. Reviewers move active to review when reviewing. Engineers move review back to active when fixing findings. The Guardian moves review to completed on a successful commit. Each agent verifies the expected state before acting, so status changes are auditable and never drift silently.

**Andon cord**: Every agent acts as a quality stop-signal. If something is off (a task in the wrong status, uncommitted changes from a previous task, validation failures that cannot be resolved, any warning or error signal), the agent pulls the andon cord: stop work and escalate to the team lead. The team lead treats andon cord escalations as highest priority and resolves them before any other work continues.

**Architect coherence across task sets**: A persistent architect agent tracks how implementation evolves. Engineers discuss with the architect when they need to diverge from the plan during development. After each commit, the architect reads divergence notes and updates upcoming tasks when the implementation reveals something that changes future plans.

**Continuous regression testing**: A regression tester runs visual and functional tests via Claude in Chrome browser automation throughout the implementation, catching UI regressions and console errors in real time.

**Cross-team collaboration**: Agents talk to each other directly, not through the team lead. If the frontend engineer needs a backend API change, they message the backend engineer, wait for implementation, then continue. QA notifies engineers when tests expose bugs. The team lead stays out of the loop on day-to-day chatter and only steps in to redirect or unblock.

**No context window exhaustion**: With specialized agents for each domain, no single agent accumulates context bloat. Fresh engineer and reviewer pairs are spawned for each task set, always starting with a clean context window while persistent agents (guardian, architect, regression tester) maintain continuity across the feature.

**Tool-agnostic product management**: A single `PRODUCT_MANAGEMENT_TOOL` configuration value maps generic `[feature]`, `[task]`, and `[subtask]` terminology to the chosen tool. Configure the MCP server for your tool once and you are set. Agents resolve tool-specific details (status names, API shapes, ID formats) via `.claude/reference/product-management/{tool}.md` at runtime. Swap tools without changing agents.

## Agent roles

**Team lead** (launched via the developer CLI):
- Coordinates the full agent team. Spawns all sub-agents, delegates work, and tracks progress. Never writes code directly

**Persistent agents** (alive for the whole feature):
- **guardian**: Owns all git commits, Aspire restarts, and final validation. Zero tolerance for failures
- **architect**: Tracks implementation evolution across task sets, reviews divergence notes, and updates upcoming tasks
- **regression-tester**: Continuous visual and functional testing via Claude in Chrome
- **researcher**: Investigation specialist. Spawned on the first research request and reused for subsequent questions. Reports findings but never writes code

**Fresh agents** (spawned per task set by the team lead):
- **backend**, **frontend**, **qa**: Engineers who implement code within their specialty
- **backend-reviewer**, **frontend-reviewer**, **qa-reviewer**: Zero-tolerance gatekeepers who review line-by-line, then send one approval message per track to the Guardian for atomic staging and commit

## How to use

This workflow requires Claude Code and will not work with other AI coding assistants.

### 1. Create a feature branch

```bash
git checkout -b feature-name
```

### 2. Configure your product management tool (one-time setup)

Pick a tool. The name must match a file in [.claude/reference/product-management/](./.claude/reference/product-management/): `Linear`, `AzureDevOps`, `Jira`, or `Markdown` (for tracking features and tasks as markdown files in your repo).

Set the value in [AGENTS.md](./AGENTS.md):

```
PRODUCT_MANAGEMENT_TOOL="Linear"
```

Then configure the MCP server for that tool. Each reference file contains the exact MCP configuration under its **MCP Configuration** section (e.g., [Linear.md](./.claude/reference/product-management/Linear.md)). The `Markdown` option needs no MCP, since it reads and writes local files directly.

### 3. Define your feature

Start the team lead agent using the [Developer CLI](#2-optional-install-the-developer-cli):

```bash
pp claude-agent team-lead
```

Use the `/create-prd` skill. The team lead will guide you through a brief interview to understand what you want to build, then generate a complete feature specification with tasks in your configured product management tool (Linear, Azure DevOps, Jira, GitHub, or markdown files).

### 4. Let the team lead take over

Tell the team lead which feature to implement by providing the title or ID. From here, the team lead spawns all the agents automatically: guardian, architect, regression tester, and fresh engineer/reviewer pairs for each task set.

Backend and frontend engineers work in parallel. QA writes tests alongside implementation and runs them once backend and frontend are approved and staged. Reviewers scrutinize every change line by line and send one approval message per track. The Guardian runs the pre-commit pipeline (build, test, format, lint, Aspire restart, smoke tests) once all tracks are staged, then commits in dependency order (backend, frontend, E2E).

## Ad-hoc work without the agent team

For smaller tasks, bug fixes, or exploratory work that don't need the full agent team, use the pair programmer instead:

```bash
pp claude-agent pair-programmer
```

The pair programmer is a standalone agent that works directly with you as a hands-on collaborator. It is not part of the agent team workflow above. It can spawn sub-agents when the task benefits from parallel work or code review.

# Inside Our Monorepo

EuropeanPlatform is a [monorepo](https://en.wikipedia.org/wiki/Monorepo) containing all application code, infrastructure, tools, libraries, documentation, etc. A monorepo is a powerful way to organize a codebase, used by Google, Facebook, Uber, Microsoft, etc.

```bash
.
├─ .claude               # Claude Code agent definitions and team configurations
│  ├─ agents             # Agent Teams agent definitions (team-lead, engineers, reviewers, etc.)
│  ├─ commands           # Slash commands and workflows
│  ├─ hooks              # Claude Code hooks to enforce MCP tool usage and prevent dangerous git operations
│  └─ rules              # AI rules for code generation patterns
├─ .github               # GitHub configuration and CI/CD workflows
├─ application           # Contains the application source code
│  ├─ AppHost            # Aspire project starting app and all dependencies in Docker
│  ├─ AppGateway         # Main entry point for the app using YARP as a reverse proxy
│  ├─ main               # Primary SCS and shell app -- build your product here
│  │   ├─ WebApp         # React SPA frontend using TypeScript and ShadCN 2.0 with Base UI
│  │   ├─ Api            # Presentation layer exposing the API to WebApp or other clients
│  │   ├─ Core           # Core business logic, application use cases, and infrastructure
│  │   ├─ Workers        # Background workers for long-running tasks and event processing
│  │   └─ Tests          # Tests for the Api, Core, and Workers
│  ├─ account            # Federated module for authentication, user and account management
│  │   ├─ WebApp         # React SPA loaded into main via Module Federation
│  │   ├─ Api            # Presentation layer exposing the API and the internal back-office API
│  │   ├─ Core           # Core business logic, application use cases, and infrastructure
│  │   ├─ Workers        # Background workers for long-running tasks and event processing
│  │   ├─ Migrator       # Run-once database migrator, deployed as a pre-upgrade Job
│  │   └─ Tests          # Tests for the Api, Core, and Workers
│  ├─ back-office        # Staff surface for operations and support, on its own hostname
│  │   ├─ WebApp         # React SPA served to staff (OIDC sign-in, mock selector in development)
│  │   ├─ Api            # BFF over the feature systems' internal APIs
│  │   ├─ Core           # Staff identity, delegation, and BFF use cases
│  │   └─ Tests          # System tests over the full BFF pipeline
│  ├─ shared-kernel      # Reusable components and default configuration for all systems
│  └─ shared-webapp      # Reusable ShadCN 2.0 components with Base UI that affect all systems
├─ cloud-infrastructure  # Infrastructure as Code (Terraform) and cluster operation scripts for Scaleway
│  └─ scaleway           # bootstrap (state bucket, registry) and environment (VPC, Kapsule, RDB,
│                        #   Object Storage, Secret Manager, Edge Services, IAM) modules
│                        #   plus cluster-ops scripts (gateway LB adoption, failover, docs)
└─ developer-cli         # A .NET CLI tool for automating common developer tasks
```

** A [Self-Contained System](https://scs-architecture.org/) is a large microservice (or a small monolith) that contains the full stack, including frontend, background jobs, etc. The `main` SCS is the shell application with catch-all routing where you build your product. The `account` SCS is loaded into main via Module Federation, enabling seamless navigation between product pages and account pages without full page reloads. The `back-office` SCS is the staff surface for operations and support on its own hostname — a backend-for-frontend over the feature systems' internal APIs, with its own staff identity (MockEasyAuth on staging and in development until a proper OIDC provider is configured).

# Technologies

### .NET 10 Backend With Vertical Sliced Architecture, DDD, CQRS, Minimal API, and Aspire

The backend is built using the most popular, mature, and commonly used technologies in the .NET ecosystem:

- [.NET 10](https://dotnet.microsoft.com) and [C# 14](https://learn.microsoft.com/en-us/dotnet/csharp/tour-of-csharp)
- [Aspire](https://aka.ms/dotnet-aspire)
- [YARP](https://microsoft.github.io/reverse-proxy)
- [ASP.NET Minimal API](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/minimal-apis)
- [Entity Framework](https://learn.microsoft.com/en-us/ef)
- [MediatR](https://github.com/jbogard/MediatR)
- [FluentValidation](https://fluentvalidation.net)
- [Mapster](https://github.com/MapsterMapper/Mapster)
- [XUnit](https://xunit.net), [FluentAssertions](https://fluentassertions.com), [NSubstitute](https://nsubstitute.github.io), and [Bogus](https://github.com/bchavez/Bogus)
- [SonarCloud](https://sonarcloud.io) and [JetBrains Code style and Cleanup](https://www.jetbrains.com/help/rider/Code_Style_Assistance.html)

<details>

<summary>Read more about the backend architecture</summary>

- **Vertical Slice Architecture**: The codebase is organized around vertical slices, each representing a feature or module, promoting separation of concerns and maintainability.
- **Domain-Driven Design (DDD)**: DDD principles are applied to ensure a clear and expressive domain model.
- **Command Query Responsibility Segregation (CQRS)**: This clearly separates read (query) and write (command) operations, adhering to the single responsibility principle (each action is in a separate command).
- **Screaming architecture**: The architecture is designed with namespaces (folders) per feature, making the concepts easily visible and expressive, rather than organizing the code by types like models and repositories.
- **MediatR pipelines**: MediatR pipeline behaviors are used to ensure consistent handling of cross-cutting concerns like validation, unit of work, and handling of domain events.
- **Strongly Typed IDs**: The codebase uses strongly typed IDs, which are a combination of the entity type and the entity ID. This is even at the outer API layer, and Swagger translates this to the underlying contract. This ensures type safety and consistency across the codebase.
- **JetBrains Code style and Cleanup**: JetBrains Rider/ReSharper is used for code style and automatic cleanup (configured in `.DotSettings`), ensuring consistent code formatting. No need to discuss tabs vs. spaces anymore; Invalid formatting breaks the build.
- **Self-contained systems**: The codebase is organized into self-contained systems. A self-contained system is a large microservice (or a small monolith) that contains the full stack including frontend, background jobs, etc. These can be developed, tested, deployed, and scaled in isolation, making it a good compromise between a large monolith and many small microservices. Unlike the popular backend-for-frontend (BFF) style with one shared frontend, this allows teams to work fully independently. The main SCS is the shell application where you build your product.
- **Shared Kernel**: The codebase uses a shared kernel for all the boilerplate code required to build a clean codebase. The shared kernel ensures consistency between self-contained systems, e.g., enforcing tenant isolation, auditing, tracking, implementation of tactical DDD patterns like aggregate, entities, repository base, ID generation, etc.

</details>

### React 19 Frontend With TypeScript, ShadCN 2.0, Base UI, and Node

The frontend is built with these technologies:

- [React 19](https://react.dev)
- [TypeScript](https://www.typescriptlang.org)
- [ShadCN 2.0](https://ui.shadcn.com) with [Base UI](https://base-ui.com)
- [Tanstack Router](https://tanstack.com/router)
- [Tanstack Query](https://tanstack.com/query)
- [Lingui](https://lingui.dev) for internationalization (i18n)
- [oxlint](https://oxc.rs/docs/guide/usage/linter) and [oxfmt](https://oxc.rs/docs/guide/usage/formatter) for linting and formatting
- [Node](https://nodejs.org/en)

### Scaleway Infrastructure With Enterprise-Grade Security and Zero Secrets

EuropeanPlatform's cloud infrastructure is built on Scaleway managed services, defined entirely
in Terraform and deployed through the Aspire pipeline:

- [Kapsule](https://www.scaleway.com/en/docs/containers/kubernetes/) - managed Kubernetes (Cilium networking, Gateway API ingress)
- [Managed Database for PostgreSQL](https://www.scaleway.com/en/docs/manage-databases-postgres/) - one database per self-contained system on a shared instance
- [Object Storage](https://www.scaleway.com/en/docs/storage/object/) - S3-compatible blob storage (avatars, logos, the shared Data Protection keyring)
- [Secret Manager](https://www.scaleway.com/en/docs/account/secret-manager/) - all application secrets, read by workloads at runtime
- [IAM](https://www.scaleway.com/en/docs/account/iam/) - one application per workload with least-privilege access to exactly its secrets
- [Edge Services](https://www.scaleway.com/en/docs/network/edge-services/) - WAF, edge TLS, and caching in front of the cluster
- [Transactional Email](https://www.scaleway.com/en/docs/transactional-email/) - deliverability for signup and account emails
- [Container Registry](https://www.scaleway.com/en/docs/container-registry/) - the image store the Aspire pipeline pushes to

<details>

<summary>Read more about this enterprise-grade architecture</summary>

- **Infrastructure as Code everywhere**: every resource from the state bucket to the edge
  pipelines is owned by Terraform; application deployment is owned by the Aspire
  publish/deploy pipeline. Nothing is created by hand, and `terraform plan` + the
  `edge-health` developer-cli command verify the environment.
- **Enterprise-grade security with zero secrets in the codebase**:
  - **Per-workload identities**: every workload runs with its own IAM application whose policy
    grants access to exactly its Secret Manager path — a compromised workload cannot read
    another's secrets.
  - **Secrets in Secret Manager**: connection strings, signing keys, and API keys are
    provisioned by Terraform and read at runtime; nothing sensitive lives in the repository,
    images, or Helm charts.
  - **Shared Data Protection keyring encrypted at rest**: the keyring that spans
    self-contained systems is encrypted with a Secret Manager key before it is persisted to
    Object Storage.
- **Automatic certificate management**: TLS certificates are requested and renewed
  automatically — managed certificates at the edge, cert-manager with the Gateway API inside
  the cluster.
- **Multiple environments**: each environment (staging, production, ...) is its own Scaleway
  project, so cost and blast radius are tracked per environment.
- **Kapsule**: the application runs on managed Kubernetes with Cilium's Gateway API (Envoy) for
  ingress — one entry point per public hostname with hostname-based routing to the
  self-contained systems.
- **PostgreSQL**: the database is a managed PostgreSQL instance with one database per
  self-contained system, keeping system boundaries hard and per-system restores trivial.
- **Scaling**: node pools scale with load, and the cost profile starts at a few tens of euros
  per month for a staging environment.

</details>

# Screenshots

Deploy an environment and open the Scaleway console to see the running stack: one project per
environment with its Kapsule cluster, managed PostgreSQL, Object Storage buckets, Edge Services
pipelines, and Secret Manager. Run `edge-health` from the developer CLI for a full read-only
health report of the edge and the public endpoints.
