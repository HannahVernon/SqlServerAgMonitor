# SQL Server AG Monitor — Installation Guide

This guide walks through installing, upgrading, and uninstalling the SQL Server AG Monitor service, and connecting the desktop app.

---

## System Requirements

| Requirement | Details |
|---|---|
| **Operating System** | Windows Server 2016 or later, or Windows 10/11 |
| **Privileges** | Local administrator (for service registration) |
| **SQL Server** | 2016 or later with Always On Availability Groups or Distributed Availability Groups |
| **.NET Runtime** | Not required — the installer publishes a self-contained binary |

### SQL Server Permissions

The monitoring account needs two server-level permissions on **every** SQL Server instance you want to monitor:

| Permission | Required For |
|---|---|
| `VIEW SERVER STATE` | DMV access — replica states, database sync status, LSN values, send/redo queues |
| `VIEW ANY DEFINITION` | Catalog view visibility — `sys.availability_groups`, `sys.availability_replicas` |

Both permissions are required. Without `VIEW ANY DEFINITION`, catalog views silently return zero rows even if `VIEW SERVER STATE` is granted.

The installer generates a ready-to-run T-SQL script (`grant-permissions.sql`) at the end of installation.

---

## Pre-Installation Checklist

Before running the installer, decide on:

1. **Service account** — `LOCAL SERVICE` (default, simplest) or a domain account (needed for Windows-authenticated SQL connections across the network).

2. **TLS certificate** (optional) — If you want HTTPS between the desktop app and the service:
   - Certificate must have a **software-based private key** (not TPM-backed, such as Azure AD device certs).
   - Certificate must be in the `LocalMachine\My` store, or available as a `.pfx` file.
   - The service account needs read access to the private key (the installer handles this).

3. **Port** — Default is `58432`. Choose a different port if that is already in use.

---

## Running the Installer

Launch `SqlAgMonitor.Installer.exe` as an administrator. The installer is a 7-step wizard.

### Step 1 — Install Location

Choose where to install the service files.

- **Default:** `C:\Program Files\SqlAgMonitor`
- The installer runs `dotnet publish` to produce a self-contained executable in this directory.
- If upgrading, the path is auto-detected from the existing service registration.

### Step 2 — Service Account

Choose which Windows account the service runs under.

| Option | When to Use |
|---|---|
| **LOCAL SERVICE** (default, recommended) | Local monitoring or SQL Auth connections. Simplest setup — no password needed. |
| **Custom domain account** | Windows-authenticated SQL connections. The account must exist in Active Directory. |

When a custom domain account is selected:
- Enter the account name (e.g., `MVCT\SqlAgMonitor`) and password.
- The installer automatically grants the **Log on as a service** right (`SeServiceLogonRight`) to the account.

### Step 3 — Service Port

The TCP port the service listens on for SignalR connections from desktop clients.

- **Default:** `58432`
- **Range:** 1024–65535

### Step 4 — TLS / HTTPS

Optionally enable TLS encryption for the service endpoint.

- **Disabled by default.** Enable the checkbox to configure.
- **Certificate Store** (recommended): Select a certificate from `LocalMachine\My`. The installer filters for non-expired certificates with private keys and warns about TPM-backed keys.
- **.pfx File**: Enter the path to a `.pfx` certificate file.

If using a self-signed certificate, the installer offers to trust it for the initial admin user setup connection.

### Step 5 — Windows Firewall

Optionally create a Windows Firewall inbound rule for the service port.

- **Enabled by default.**
- Choose **Allow from any source** (default) or **Restrict to IP or subnet** (e.g., `192.168.1.0/24`).
- Rule name: `SqlAgMonitor Service (TCP {port})`

### Step 6 — Admin Credentials

Create the initial admin account used by desktop clients to authenticate with the service.

- **Username:** defaults to `admin`
- **Password:** minimum 8 characters, must be confirmed
- **Important:** Note these credentials — you will enter them in the desktop app's Settings → Service tab.

This step is skipped on upgrade if you choose to keep existing settings.

### Step 7 — Ready to Install

Review all settings in the summary panel, then click **Install** (or **Upgrade**).

The installer performs these actions in order:

1. Validates the install path (prevents duplicate service registrations at different paths)
2. Stops the existing service (if upgrading)
3. Publishes the service binary (`dotnet publish`)
4. Writes `appsettings.json` with port, TLS, and logging configuration
5. Grants the service account read access to the TLS certificate private key (if applicable)
6. Creates or reconfigures the Windows Service via Win32 API
7. Starts the service (waits up to 120 seconds)
8. Creates the Windows Firewall rule (if enabled)
9. Creates the admin user via the service REST API
10. Registers the service in Add/Remove Programs
11. Generates the `grant-permissions.sql` script

If any step fails, the error is displayed with a **Copy to Clipboard** button. The installer tracks completed actions so you know what to clean up if you cancel partway through.

### Completion Screen

After successful installation:

- A **SQL Permissions warning** reminds you to run `grant-permissions.sql` on each monitored SQL Server instance.
- The **📋 Copy Script to Clipboard** button copies the T-SQL script for easy pasting into SSMS.
- **Next steps** are displayed — how to connect the desktop app.

---

## Post-Installation

### 1. Run the Grant Script

Open the generated `grant-permissions.sql` in SQL Server Management Studio and execute it on **every** SQL Server instance that hosts an AG or DAG you want to monitor. The script:

- Creates a server login for the service account (if it doesn't exist)
- Grants `VIEW SERVER STATE` and `VIEW ANY DEFINITION`

### 2. Connect the Desktop App

1. Open the SQL AG Monitor desktop application.
2. Go to **Settings → Service** tab.
3. Enable **Service Client Mode**.
4. Enter the service host (e.g., `localhost`), port (e.g., `58432`), and the admin credentials from Step 6.
5. Click **Test Connection** to verify.

### 3. Verify Monitoring

Add your Availability Groups or Distributed Availability Groups in the desktop app. Each tab connects to the configured SQL Server instances and displays real-time replica status, synchronization health, LSN progress, and send/redo queue sizes.

---

## Upgrading

Re-run the installer. It automatically detects the existing installation and pre-populates all settings.

- **Default behavior:** Keep existing settings — the admin credentials step is skipped and your configuration is preserved.
- **Clean install option:** Check "Perform a clean install" on the Welcome screen to re-enter all settings.

During upgrade:
1. The existing service is stopped.
2. Service files are replaced with the new version.
3. If the service account or start type changed, you are prompted to confirm reconfiguration.
4. The service is restarted.
5. Your monitoring configuration, admin credentials, and alert history are preserved.

---

## Uninstalling

### Via Windows Settings

Open **Settings → Apps → Installed Apps**, find **SQL Server AG Monitor Service**, and click **Uninstall**.

### What Gets Removed

1. The Windows Service (`SqlAgMonitorService`) is stopped and deleted
2. Published files in the install directory are removed
3. Service data in `%APPDATA%\SqlAgMonitor\service\` is removed (credentials, database files)
4. The Add/Remove Programs registry entry is removed

### What Is NOT Removed

- The desktop application (it is a standalone app, not installed by the service installer)
- Desktop app configuration in `%APPDATA%\SqlAgMonitor\`
- The Windows Firewall rule (remove manually via `wf.msc` or `netsh`)
- SQL Server logins and permissions created by the grant script

---

## Manual Installation (Advanced)

PowerShell scripts are available in the `scripts/` directory for advanced users who prefer command-line installation.

```powershell
# 1. Publish the service binary
.\scripts\Publish-Service.ps1 -OutputPath "C:\Program Files\SqlAgMonitor"

# 2. Register as a Windows Service
.\scripts\Install-Service.ps1

# With a domain account:
.\scripts\Install-Service.ps1 -ServiceAccount "DOMAIN\svc_agmonitor" -ServicePassword "YourPassword"

# 3. Uninstall
.\scripts\Uninstall-Service.ps1
```

After manual installation, you must:
- Create `appsettings.json` in the install directory (see `FEATURE-GUIDE.md` for format)
- Run the service: `net start SqlAgMonitorService`
- Create the admin user via `POST /api/auth/setup` with `{"username":"admin","password":"YourPassword"}`
- Run the grant script on each SQL Server instance

---

## Troubleshooting

### Service Won't Start

- Check the service log at `%ProgramData%\SqlAgMonitor\logs\service-{date}.log`
- Verify the service account has the **Log on as a service** right (the installer grants this automatically for domain accounts)
- If using TLS, verify the service account can read the certificate's private key

### Desktop App Can't Connect

- Verify the service is running: `Get-Service SqlAgMonitorService`
- Check the firewall rule allows inbound TCP on the configured port
- If using TLS with a self-signed certificate, accept the certificate prompt in the desktop app
- Verify the correct host, port, username, and password in Settings → Service

### No Data Returned (Green Dots but Empty Grid)

- Both `VIEW SERVER STATE` and `VIEW ANY DEFINITION` must be granted on each SQL Server
- `sysadmin` bypasses permission checks, so this typically affects non-admin service accounts
- Run `grant-permissions.sql` on every monitored SQL Server instance

### Installer Errors

- Check the installer log at `%LOCALAPPDATA%\SqlAgMonitor\installer.log`
- **Port conflict:** Another process is using the configured port. Change the port or stop the conflicting process.
- **Certificate error:** TPM-backed certificates cannot be used. Select a software-based certificate or use a `.pfx` file.
- **Service creation failed:** Ensure you are running as administrator.

### Certificate Issues

| Symptom | Cause | Fix |
|---|---|---|
| Certificate not listed in installer | Not in `LocalMachine\My` store, expired, or has no private key | Import to the correct store with private key |
| "TPM-backed key" warning | Private key is stored in a hardware TPM | Use a software-based certificate or `.pfx` file |
| Service can't bind to certificate | Service account lacks read access to private key | Re-run the installer or manually grant access via `certlm.msc` |
