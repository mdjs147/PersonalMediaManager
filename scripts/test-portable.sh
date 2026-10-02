#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
for project in Domain Application Infrastructure.External Infrastructure.Persistence Host; do
  dotnet test "tests/PersonalMediaManager.${project}.Tests" -c Release -m:1 --filter 'Category!=Performance' --logger trx --results-directory "artifacts/test-results/${project}"
done
