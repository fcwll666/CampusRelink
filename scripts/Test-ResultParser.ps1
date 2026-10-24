#requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'BuildSupport.ps1')

$transcript = @'
PASS ordinary case
[PASS] bracketed case
PASS: colon case
PASS-hyphen case
FAIL xyz
[FAIL] bracketed failure
Failures: 0
PASSED 45 checks
FAILURE detail
Failures: 1
'@
$cases = @(ConvertFrom-CommunityTestOutput -Suite 'ParserRegression' -Output $transcript)
if ($cases.Count -ne 6) { throw "Expected six case records; found $($cases.Count)." }
if (@($cases | Where-Object { $_.Result -eq 'PASS' }).Count -ne 4) { throw 'PASS records were not parsed correctly.' }
if (@($cases | Where-Object { $_.Result -eq 'FAIL' }).Count -ne 2) { throw 'FAIL records were not parsed correctly.' }
if (@($cases | Where-Object { $_.Case -eq 'xyz' -and $_.Result -eq 'FAIL' }).Count -ne 1) { throw 'The explicit FAIL xyz case was lost.' }
if (@($cases | Where-Object { $_.Case -like '*ures:*' -or $_.Case -like '*URE detail*' }).Count -ne 0) { throw 'A summary line was incorrectly parsed as a case.' }
Write-Host 'PASS result parser ignores Failures summaries and preserves explicit FAIL cases.'
