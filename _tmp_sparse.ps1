$ErrorActionPreference = 'Continue'

# 1. no-cone variant, must match cone behaviour and speed
$tmp = Join-Path $env:TEMP "gk-sparse-b"
Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue

$sw = [Diagnostics.Stopwatch]::StartNew()
git clone --progress --filter=blob:none --no-checkout --sparse --depth 1 --branch "21.0.3" https://github.com/angular/angular $tmp 2>&1 | Out-Null
$cloneSec = [math]::Round($sw.Elapsed.TotalSeconds, 1)

$sw.Restart()
git -C $tmp sparse-checkout set --no-cone "adev/src/content" 2>&1 | Out-Null
$sparseOk = $LASTEXITCODE
$sparseSec = [math]::Round($sw.Elapsed.TotalSeconds, 1)

$sw.Restart()
git -C $tmp checkout HEAD 2>&1 | Out-Null
$checkoutSec = [math]::Round($sw.Elapsed.TotalSeconds, 1)

$files = Get-ChildItem (Join-Path $tmp "adev/src/content") -Recurse -File -ErrorAction SilentlyContinue
"no-cone: clone=${cloneSec}s sparse=${sparseSec}s(rc=$sparseOk) checkout=${checkoutSec}s files=$($files.Count)"
Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue

# 2. sparse-checkout set with a FILE path (docs_path pointing at a single file)
$tmp2 = Join-Path $env:TEMP "gk-sparse-c"
Remove-Item $tmp2 -Recurse -Force -ErrorAction SilentlyContinue
git clone --quiet --filter=blob:none --no-checkout --sparse --depth 1 --branch "21.0.3" https://github.com/angular/angular $tmp2 2>&1 | Out-Null
git -C $tmp2 sparse-checkout set "adev/src/content/guide" 2>&1 | Out-Null
"cone   dir  path rc=$LASTEXITCODE"
git -C $tmp2 sparse-checkout set --no-cone "package.json" 2>&1 | Out-Null
"nocone file path rc=$LASTEXITCODE"
git -C $tmp2 checkout HEAD 2>&1 | Out-Null
"package.json materialised: $(Test-Path (Join-Path $tmp2 'package.json'))"
Remove-Item $tmp2 -Recurse -Force -ErrorAction SilentlyContinue

# 3. ls-remote budget used by GitReferenceProvider (30s)
$sw = [Diagnostics.Stopwatch]::StartNew()
$tags = @(git ls-remote --refs --tags https://github.com/angular/angular 2>$null)
$sw.Stop()
"ls-remote: $([math]::Round($sw.Elapsed.TotalSeconds,1))s tags=$($tags.Count)"
