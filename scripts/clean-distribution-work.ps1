#requires -Version 7.5
[CmdletBinding()]
param([string]$WorkRoot, [switch]$Execute)
. (Join-Path $PSScriptRoot 'distribution-workspace.ps1')
# Dry-run by default; no disk-wide search, legacy adoption or final artifact cleanup.
Clear-StaleDistributionWork $WorkRoot -Execute:$Execute
