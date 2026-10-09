param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $ContextKitArgs
)

$project = Join-Path $PSScriptRoot "src/ContextKit.Cli/ContextKit.Cli.csproj"
dotnet run --project $project --no-restore -v:q -- @ContextKitArgs
exit $LASTEXITCODE