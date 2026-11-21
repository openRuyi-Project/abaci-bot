$ErrorActionPreference = 'Stop'

$commit = (git rev-parse --short=12 HEAD).Trim()
$dirty = if (git status --porcelain) { 'true' } else { 'false' }

docker build `
    --build-arg "GIT_COMMIT=$commit" `
    --build-arg "GIT_DIRTY=$dirty" `
    @args .
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
