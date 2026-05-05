# Insighta CLI

Globally installable CLI for Insighta Labs+.

## Install

```bash
dotnet tool install --global HngInsightaLabs.Cli
```

The NuGet package name is `HngInsightaLabs.Cli`; the installed command is:

```bash
insighta
```

To update:

```bash
dotnet tool update --global HngInsightaLabs.Cli
```

## Local Development Install

```bash
dotnet pack -c Release
dotnet tool install --global --add-source ./bin/Release HngInsightaLabs.Cli
```

## Login

For a fresh login, the CLI reads the backend URL from `INSIGHTA_BACKEND_URL` in `.env`. It also accepts the backend's `Auth__BackendPublicUrl` value as a fallback.

```bash
INSIGHTA_BACKEND_URL=http://localhost:8080
```

After installing, log in directly:

```bash
insighta login
```

Credentials are stored at `~/.insighta/credentials.json`.

## Optional Backend Override

You can still store a manual backend override:

```bash
insighta config set-backend https://your-backend-url.com
```

For local Docker testing:

```bash
insighta config set-backend http://localhost:8080
```

## Commands

```bash
insighta login
insighta logout
insighta whoami
insighta profiles list --gender male --page 1 --limit 10
insighta profiles list --country NG --age-group adult
insighta profiles search "young males from nigeria"
insighta profiles get <id>
insighta profiles create --name "Ada Lovelace"
insighta profiles delete <id>
insighta profiles export --format csv
insighta profiles export --format csv --gender male --country NG
```

Exports are saved as a timestamped CSV in the current working directory:

```bash
insighta profiles export
```
