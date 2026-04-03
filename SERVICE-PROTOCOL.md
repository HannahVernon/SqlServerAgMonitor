# SQL Server AG Monitor — Service Protocol Reference

This document describes the REST and SignalR API exposed by the **SqlAgMonitor Windows Service**. Any client (desktop, mobile, CLI) can integrate with the service by following this protocol.

**Protocol version:** `1`

---

## Overview

The service exposes:

- **REST endpoints** for authentication, version negotiation, and configuration management
- **A SignalR hub** (`/monitor`) for real-time snapshot and alert push events, plus on-demand queries

All authenticated endpoints require a JWT bearer token obtained from `POST /api/auth/login`.

### Base URL

```
{scheme}://{host}:{port}
```

- **scheme:** `https` (recommended) or `http`
- **port:** Configurable, default `58432`

### TLS Certificate Pinning

When the service uses a self-signed or internal CA certificate, clients should:

1. Probe the endpoint and capture the server certificate
2. Present the certificate details (subject, issuer, thumbprint, expiry) to the user
3. If the user accepts, pin the thumbprint and validate future connections against it

---

## Authentication

All authenticated requests use the `Authorization: Bearer <token>` header.  
SignalR connections pass the token via query string: `?access_token=<token>`.

Tokens are issued by `POST /api/auth/login` and signed with an HMAC-SHA256 key managed by the service.

---

## REST Endpoints

### `GET /api/version`

**Authentication:** None (public)

Returns the service protocol version. Clients **must** call this before login to verify compatibility.

**Response (200 OK):**

```json
{
  "protocolVersion": 1,
  "serviceName": "SqlAgMonitor.Service"
}
```

**Client behavior:**

- If the endpoint returns `404`, the service predates protocol versioning and must be updated.
- If `protocolVersion` is less than the client's expected version, show an upgrade prompt.

---

### `POST /api/auth/login`

**Authentication:** None (public)

Authenticates a user and returns a JWT token.

**Request:**

```json
{
  "username": "admin",
  "password": "secretpassword"
}
```

**Response (200 OK):**

```json
{
  "token": "<jwt_token>"
}
```

**Error responses:**

| Status | Condition |
|--------|-----------|
| `400`  | Username or password is empty |
| `401`  | Invalid credentials |

---

### `POST /api/auth/setup`

**Authentication:** None (initial setup only)

Creates the first admin user. Only works when the user store is empty.

**Request:**

```json
{
  "username": "admin",
  "password": "secretpassword"
}
```

**Response (200 OK):**

```json
{
  "message": "User 'admin' created."
}
```

**Error responses:**

| Status | Condition |
|--------|-----------|
| `400`  | Missing fields or password < 8 characters |
| `409`  | Users already exist |

---

### `GET /api/config/export`

**Authentication:** Required

Returns the service configuration with credential keys redacted.

**Response (200 OK):**

```json
{
  "monitoredGroups": [
    {
      "name": "MyAG",
      "groupType": "AvailabilityGroup",
      "pollingIntervalSeconds": 10,
      "connections": [
        {
          "server": "sql-primary.example.com",
          "authType": "windows",
          "username": null,
          "credentialKey": null,
          "encrypt": true,
          "trustServerCertificate": false
        }
      ],
      "alertOverrides": {},
      "mutedAlerts": []
    }
  ],
  "alerts": { "masterCooldownMinutes": 5 },
  "email": {
    "enabled": false,
    "smtpServer": "",
    "smtpPort": 587,
    "useTls": true,
    "fromAddress": "",
    "toAddresses": [],
    "username": null,
    "credentialKey": null
  },
  "syslog": { "enabled": false, "server": "", "port": 514, "protocol": "UDP" }
}
```

> **Note:** `credentialKey` fields are always `null` in the export — passwords are never exposed over the API.

---

### `POST /api/config/import`

**Authentication:** Required

Merges configuration into the service. All fields are optional — only provided sections are imported.

**Request:**

```json
{
  "monitoredGroups": [ ... ],
  "alerts": { ... },
  "email": { ... },
  "syslog": { ... }
}
```

- **Groups** are matched by name (case-insensitive). Existing groups are updated; new groups are added.
- **Alerts**, **email**, and **syslog** sections replace the existing values wholesale.

**Response (200 OK):**

```json
{
  "imported": {
    "groups": 3,
    "alerts": true,
    "email": true,
    "syslog": false
  }
}
```

---

## SignalR Hub

**URL:** `/monitor`  
**Authentication:** Required — pass JWT via query string: `/monitor?access_token=<token>`  
**Transport:** WebSocket (preferred), Server-Sent Events, Long Polling

### Reconnection Policy

Clients should implement automatic reconnection with exponential backoff:

| Attempt | Delay |
|---------|-------|
| 0       | 0 s   |
| 1       | 2 s   |
| 2       | 5 s   |
| 3       | 10 s  |
| 4       | 30 s  |
| 5+      | 60 s  |

On reconnection, clients **must** call `GET /api/version` to verify the service protocol version has not changed.

### Client → Server Methods

These methods are invoked by the client on the hub connection.

#### `GetMonitoredGroups() → List<MonitoredGroupInfo>`

Returns the list of monitored AG/DAG groups.

```json
[
  { "name": "MyAG", "groupType": "AvailabilityGroup" },
  { "name": "MyDAG", "groupType": "DistributedAvailabilityGroup" }
]
```

#### `GetCurrentSnapshots() → List<MonitoredGroupSnapshot>`

Returns the latest snapshot for each monitored group. See [MonitoredGroupSnapshot](#monitoredgroupsnapshot) for the payload shape.

#### `GetSnapshotHistory(since, until, groupName?, replicaName?, databaseName?) → List<SnapshotDataPoint>`

Returns historical snapshot data points for trend analysis.

| Parameter      | Type             | Required | Description |
|---------------|------------------|----------|-------------|
| `since`       | `DateTimeOffset` | Yes      | Start of range |
| `until`       | `DateTimeOffset` | Yes      | End of range |
| `groupName`   | `string?`        | No       | Filter by group |
| `replicaName` | `string?`        | No       | Filter by replica |
| `databaseName`| `string?`        | No       | Filter by database |

#### `GetSnapshotFilters(groupName?, replicaName?) → SnapshotFilterOptions`

Returns distinct values for populating filter dropdowns.

```json
{
  "groupNames": ["MyAG", "MyDAG"],
  "replicaNames": ["sql-primary", "sql-secondary"],
  "databaseNames": ["AppDb", "ReportDb"]
}
```

#### `GetAlertHistory(groupName?, since?, limit?) → List<AlertEvent>`

Returns alert history. See [AlertEvent](#alertevent) for the payload shape.

| Parameter   | Type              | Required | Default | Description |
|------------|-------------------|----------|---------|-------------|
| `groupName`| `string?`         | No       | all     | Filter by group |
| `since`    | `DateTimeOffset?` | No       | all     | Events after this time |
| `limit`    | `int`             | No       | 500     | Max results (1–5000) |

#### `ExportToExcel(since, until, groupName?, replicaName?, databaseName?) → byte[]`

Returns an Excel workbook as a byte array. Parameters match `GetSnapshotHistory`.

### Server → Client Events

These events are pushed by the service to all connected clients.

#### `OnSnapshotReceived(groupName: string, snapshot: MonitoredGroupSnapshot)`

Pushed whenever a monitored group is polled. Contains the full current state.

#### `OnAlertFired(alert: AlertEvent)`

Pushed when an alert condition is detected.

#### `OnConnectionStateChanged(groupName: string, state: string)`

Pushed when a monitored group's SQL Server connection state changes. `state` values: `"Connected"`, `"Disconnected"`.

#### `OnConfigurationChanged()`

Pushed when the service configuration changes (e.g. after a config import). Clients should reload their snapshots via `GetCurrentSnapshots` to pick up newly added or removed groups.

---

## Data Models

### MonitoredGroupSnapshot

```json
{
  "name": "MyAG",
  "groupType": "AvailabilityGroup",
  "timestamp": "2026-04-02T19:00:00+00:00",
  "agInfo": { ... },
  "dagInfo": null,
  "overallHealth": "Healthy",
  "errorMessage": null,
  "isConnected": true
}
```

| Field          | Type                   | Description |
|---------------|------------------------|-------------|
| `name`        | `string`               | Group name |
| `groupType`   | `string` enum          | `"AvailabilityGroup"` or `"DistributedAvailabilityGroup"` |
| `timestamp`   | `DateTimeOffset`       | When the snapshot was captured |
| `agInfo`      | `AvailabilityGroupInfo?` | AG details (null for DAGs) |
| `dagInfo`     | `DistributedAgInfo?`   | DAG details (null for AGs) |
| `overallHealth` | `string` enum        | `"Healthy"`, `"Warning"`, or `"Unhealthy"` |
| `errorMessage`| `string?`              | Error details if connection failed |
| `isConnected` | `bool`                 | Whether the group's SQL connection is active |

### AlertEvent

```json
{
  "id": 42,
  "timestamp": "2026-04-02T19:00:00+00:00",
  "alertType": "SynchronizationLag",
  "groupName": "MyAG",
  "replicaName": "sql-secondary",
  "databaseName": "AppDb",
  "message": "Synchronization lag exceeded threshold",
  "severity": "Warning",
  "emailSent": true,
  "syslogSent": false
}
```

| Field          | Type             | Description |
|---------------|------------------|-------------|
| `id`          | `long`           | Unique event ID |
| `timestamp`   | `DateTimeOffset` | When the alert was triggered |
| `alertType`   | `string` enum    | Alert type (e.g., `SynchronizationLag`, `RoleChange`, `ConnectionLost`) |
| `groupName`   | `string`         | AG/DAG name |
| `replicaName` | `string?`        | Replica name (if applicable) |
| `databaseName`| `string?`        | Database name (if applicable) |
| `message`     | `string`         | Human-readable description |
| `severity`    | `string` enum    | `"Information"`, `"Warning"`, or `"Critical"` |
| `emailSent`   | `bool`           | Whether an email notification was dispatched |
| `syslogSent`  | `bool`           | Whether a syslog notification was dispatched |

---

## Connection Flow

A well-behaved client should follow this sequence:

```
1. GET  /api/version          → verify protocolVersion ≥ expected
2. POST /api/auth/login       → obtain JWT token
3. Connect to /monitor hub    → pass token via ?access_token=
4. Invoke GetCurrentSnapshots → populate initial state
5. Listen for push events     → OnSnapshotReceived, OnAlertFired, OnConnectionStateChanged, OnConfigurationChanged
```

On reconnection after a connection drop:

```
1. GET /api/version           → verify service hasn't been replaced with incompatible version
2. Resume hub connection      → SignalR auto-reconnect handles this
3. Invoke GetCurrentSnapshots → refresh state after gap
```
