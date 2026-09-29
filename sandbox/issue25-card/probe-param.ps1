param([string]$SceneRoot = "$PSScriptRoot")
$ErrorActionPreference = 'Stop'
Write-Output ("SceneRoot=[" + $SceneRoot + "]  PSScriptRoot=[" + $PSScriptRoot + "]  PSCommandPath=[" + $PSCommandPath + "]")
