# Building and Pushing the Container Image

MCP Gateway uses the [.NET SDK container publishing](https://learn.microsoft.com/en-us/dotnet/core/docker/publish-as-container) feature (`/t:PublishContainer`) to build and push a container image directly — **no Dockerfile required**.

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8)
- [Node.js](https://nodejs.org/) (for the management portal SPA build)
- Access to your target container registry with push permissions

## Authenticate to Your Registry

The `dotnet publish /t:PublishContainer` target pushes via the Docker credential store, so you need valid registry credentials before running the publish.

For Azure Container Registry (ACR), one approach without Docker Desktop installed is to exchange an Azure token for a registry refresh token and write it to `~/.docker/config.json`:

```bash
LOGIN_SERVER="<your-registry>.azurecr.io"
REFRESH_TOKEN=$(az acr login --name <your-registry> --expose-token --query refreshToken -o tsv)
AUTH=$(echo -n "00000000-0000-0000-0000-000000000000:${REFRESH_TOKEN}" | base64 -w 0)

mkdir -p ~/.docker
cat > ~/.docker/config.json <<EOF
{
  "auths": {
    "${LOGIN_SERVER}": {
      "auth": "${AUTH}"
    }
  }
}
EOF
```

For other registries, use the standard `docker login` command or your registry's CLI equivalent.

## Build and Push

Run `dotnet publish` with the `PublishContainer` target from the repository root:

```bash
dotnet publish dotnet/Microsoft.McpGateway.Service/src/Microsoft.McpGateway.Service.csproj \
  /t:PublishContainer \
  -r linux-arm64 \
  --self-contained false \
  -p:ContainerRegistry=<your-registry-host> \
  -p:ContainerImageName=mcp-gateway \
  -p:ContainerImageTag=<version> \
  -p:BuildPortal=true
```

### Key Parameters

| Parameter | Description |
|---|---|
| `-r linux-arm64` | Target runtime identifier. Use `linux-x64` for x86-64 hosts. |
| `--self-contained false` | Use the .NET runtime from the base image rather than bundling it. |
| `-p:ContainerRegistry` | Hostname of the registry to push to (e.g. `myregistry.azurecr.io`). |
| `-p:ContainerImageName` | Repository name for the image. |
| `-p:ContainerImageTag` | Tag to apply (e.g. a semantic version). |
| `-p:BuildPortal=true` | Runs `npm ci && npm run build` in the `portal/` directory and embeds the SPA into `wwwroot/portal/`. Set to `false` to skip the portal build for faster backend-only iterations. |

## How It Works

1. **Restore** — NuGet packages are restored for the service and its `Microsoft.McpGateway.Management` project reference.
2. **Portal build** — When `BuildPortal=true`, the `BuildManagementPortal` MSBuild target runs `npm ci` followed by `npm run build` inside the `portal/` directory. The Vite output is written to `wwwroot/portal/` and included in the publish output.
3. **Publish** — The .NET SDK compiles the service for the target runtime and places all outputs under `bin/Release/net8.0/<rid>/publish/`.
4. **Container image assembly** — The SDK's `PublishContainer` target layers the published output on top of `mcr.microsoft.com/dotnet/aspnet:8.0` and produces an OCI-compliant image.
5. **Push** — The image and its layers are pushed to the configured registry. Layers that already exist in the registry are skipped automatically.

## Troubleshooting

### `npm ci` fails with a peer dependency conflict

If you see an `ERESOLVE` error from npm (e.g. a plugin that does not yet declare support for the installed version of Vite), update the relevant package version in `portal/package.json` to a release that declares the correct peer range, then run `npm install` in the `portal/` directory before re-running `dotnet publish`.

### Stale asset manifest errors

If the SDK fails to copy a portal asset because it was not found (the filename contains a content hash that changed between builds), clean the previous build output first:

```bash
dotnet clean dotnet/Microsoft.McpGateway.Service/src/Microsoft.McpGateway.Service.csproj -r linux-arm64
rm -rf dotnet/Microsoft.McpGateway.Service/src/wwwroot/portal
```

Then re-run the publish command.

### `ContainerImageName` deprecation warning

You may see `CONTAINER003: The property 'ContainerImageName' was set but is obsolete`. This is a non-fatal warning. The replacement property is `ContainerRepository`, which is supported in newer versions of the .NET SDK container tooling.
