# Repo-local development launcher for the GroundKit CLI.
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $GroundKitArgs
)

$project = Join-Path $PSScriptRoot "src/ContextKit.Cli/ContextKit.Cli.csproj"
dotnet run --project $project --no-restore -v:q -- @GroundKitArgs
exit $LASTEXITCODE
