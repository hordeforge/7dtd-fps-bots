#!/usr/bin/env bash
# Line coverage for the pure-BCL unit + fuzz suites, compiled with the dotnet
# SDK so dotnet-coverage can instrument them (the regular gate uses mcs+mono,
# whose JIT output no OSS line-coverage tool can see). Mirrors
# scripts/test-idempotency.sh suite-for-suite minus the Newtonsoft- and
# game-DLL-gated suites. Output: merged coverage.cobertura.xml at the repo
# root; the badge filters to /Source/.
set -euo pipefail
# The suite loop feeds a merged cobertura report, so the order its glob
# resolves in must not follow the runner's locale.
export LC_ALL=C TZ=UTC

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

suite() { # <name> <tests.cs> <prod.cs>...   (last arg is the suite, the rest are the sources it compiles against)
	local name="$1" tests
	shift
	tests="${*: -1}"
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
		local src
		for src in "$@"; do
			echo "    <Compile Include=\"$root/$src\" />"
		done
		echo "    <Compile Include=\"$root/$tests\" />"
		echo '  </ItemGroup>'
		echo '</Project>'
	} > "$dir/cov.csproj"
}

suite idempotency tests/BotMod.Tests/IdempotencyLedgerTests.cs Source/BotMod/Web/IdempotencyLedger.cs
suite atomictextfile tests/BotMod.Tests/AtomicTextFileTests.cs Source/BotMod/Foundation/AtomicTextFile.cs
suite idempotencyfuzz tests/BotMod.Tests/IdempotencyLedgerFuzzTests.cs Source/BotMod/Web/IdempotencyLedger.cs
suite mainthreaddispatch tests/BotMod.Tests/MainThreadDispatchTests.cs Source/BotMod/Web/MainThreadDispatch.cs
suite logsanitize tests/BotMod.Tests/LogSanitizerTests.cs Source/BotMod/Foundation/LogSanitizer.cs
suite logsanitizerfuzz tests/BotMod.Tests/LogSanitizerFuzzTests.cs Source/BotMod/Foundation/LogSanitizer.cs Source/BotMod/Foundation/BotText.cs
suite requestfields tests/BotMod.Tests/RequestFieldsTests.cs Source/BotMod/Web/RequestFields.cs Source/BotMod/Foundation/BotText.cs
suite combatgates tests/BotMod.Tests/CombatGatesTests.cs Source/BotMod/Config/CombatGates.cs
suite bottext tests/BotMod.Tests/BotTextTests.cs Source/BotMod/Foundation/BotText.cs
suite lcg tests/BotMod.Tests/LcgTests.cs Source/BotMod/Foundation/Lcg.cs
suite botargparser tests/BotMod.Tests/BotArgParserTests.cs Source/BotMod/Commands/BotArgParser.cs
suite botargparserfuzz tests/BotMod.Tests/BotArgParserFuzzTests.cs Source/BotMod/Commands/BotArgParser.cs

xmls=()
for d in "$work"/*/; do
	name="$(basename "$d")"
	pushd "$d" > /dev/null
	# The build log is kept and printed on failure: piping it into /dev/null
	# leaves a compile error with no diagnostics at all, so the only symptom
	# of a broken suite is a bare `set -e` exit.
	if ! dotnet build -c Release -v q > "$work/$name.build.log" 2>&1; then
		echo "FAIL: suite $name did not compile" >&2
		cat "$work/$name.build.log" >&2
		exit 1
	fi
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
