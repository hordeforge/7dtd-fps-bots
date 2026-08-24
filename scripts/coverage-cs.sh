#!/usr/bin/env bash
# Line coverage for the pure-BCL unit + fuzz suites, compiled with the dotnet
# SDK so dotnet-coverage can instrument them (the regular gate uses mcs+mono,
# whose JIT output no OSS line-coverage tool can see). Mirrors
# scripts/test-idempotency.sh suite-for-suite minus the Newtonsoft- and
# game-DLL-gated suites. Output: merged coverage.cobertura.xml at the repo
# root; the badge filters to /Source/.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# .scratch/, not $TMPDIR: /tmp is tmpfs on most boxes and eight dotnet
# builds land there.
mkdir -p "$root/.scratch"
work="$(mktemp -d "$root/.scratch/cov.XXXXXX")"
trap 'rm -rf "$work"' EXIT

if ! command -v dotnet >/dev/null 2>&1; then
	echo "SKIP: dotnet SDK not found; cannot run the coverage lane" >&2
	exit 0
fi
if ! command -v dotnet-coverage >/dev/null 2>&1; then
	echo "SKIP: dotnet-coverage not found (dotnet tool install -g dotnet-coverage)" >&2
	exit 0
fi

suite() { # <name> <prod.cs> <tests.cs>
	local name="$1" prod="$2" tests="$3"
	local dir="$work/$name"
	mkdir -p "$dir"
	{
		echo '<Project Sdk="Microsoft.NET.Sdk">'
		echo '  <PropertyGroup>'
		echo '    <OutputType>Exe</OutputType>'
		echo '    <TargetFramework>net8.0</TargetFramework>'
		echo '    <Nullable>disable</Nullable>'
		echo '    <ImplicitUsings>disable</ImplicitUsings>'
		echo '    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>'
		echo '  </PropertyGroup>'
		echo '  <ItemGroup>'
		echo "    <Compile Include=\"$root/$prod\" />"
		echo "    <Compile Include=\"$root/$tests\" />"
		echo '  </ItemGroup>'
		echo '</Project>'
	} > "$dir/cov.csproj"
}

suite idempotency Source/BotMod/Web/IdempotencyLedger.cs tests/BotMod.Web.Tests/IdempotencyLedgerTests.cs
suite atomictextfile Source/BotMod/Config/AtomicTextFile.cs tests/BotMod.Web.Tests/AtomicTextFileTests.cs
suite idempotencyfuzz Source/BotMod/Web/IdempotencyLedger.cs tests/BotMod.Web.Tests/IdempotencyLedgerFuzzTests.cs
suite mainthreaddispatch Source/BotMod/Web/MainThreadDispatch.cs tests/BotMod.Web.Tests/MainThreadDispatchTests.cs
suite logsanitize Source/BotMod/Config/LogSanitizer.cs tests/BotMod.Web.Tests/LogSanitizerTests.cs
suite bottext Source/BotMod/Config/BotText.cs tests/BotMod.Web.Tests/BotTextTests.cs
suite botargparser Source/BotMod/Commands/BotArgParser.cs tests/BotMod.Web.Tests/BotArgParserTests.cs
suite botargparserfuzz Source/BotMod/Commands/BotArgParser.cs tests/BotMod.Web.Tests/BotArgParserFuzzTests.cs

xmls=()
for d in "$work"/*/; do
	name="$(basename "$d")"
	pushd "$d" > /dev/null
	dotnet build -c Release -v q 2>&1 | tail -1 > /dev/null
	dll="$(find bin -name 'cov.dll' | head -1)"
	dotnet-coverage collect -f cobertura -o "$work/$name.xml" -- dotnet "$dll" > /dev/null 2>&1 || {
		echo "FAIL: suite $name under the coverage profiler" >&2
		exit 1
	}
	popd > /dev/null
	xmls+=("$work/$name.xml")
done

dotnet-coverage merge -f cobertura -o "$root/coverage.cobertura.xml" "${xmls[@]}" > /dev/null
echo "OK: $root/coverage.cobertura.xml (${#xmls[@]} suites)"
