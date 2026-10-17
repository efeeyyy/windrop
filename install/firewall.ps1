#Requires -RunAsAdministrator
<#
  WinDrop güvenlik duvarı kuralları (PROTOCOL §5). Yönetici gerektirir; yeniden çalıştırmak güvenlidir.
    "WinDrop data"          : TCP <Port> gelen, yalnız Private profil
    "WinDrop hotspot data"  : TCP <Port> gelen, tüm profiller, yalnız RemoteAddress 192.168.137.0/24
  Kurallar exe yoluna değil porta bağlıdır. -Remove kuralları siler.
#>
[CmdletBinding()]
param(
    [int]$Port = 53600,
    [switch]$Remove
)
$ErrorActionPreference = 'Stop'

function Set-WinDropRule {
    param([string]$Name, [hashtable]$Extra)
    Get-NetFirewallRule -DisplayName $Name -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    New-NetFirewallRule -DisplayName $Name -Group 'WinDrop' -Direction Inbound -Action Allow `
        -Protocol TCP -LocalPort $Port -Enabled True @Extra | Out-Null
    Write-Host "Kural eklendi: $Name"
}

if ($Remove) {
    foreach ($name in 'WinDrop data', 'WinDrop hotspot data') {
        $rules = Get-NetFirewallRule -DisplayName $name -ErrorAction SilentlyContinue
        if ($rules) { $rules | Remove-NetFirewallRule; Write-Host "Kural silindi: $name" }
        else { Write-Host "Kural yok (atlandı): $name" }
    }
} else {
    Set-WinDropRule -Name 'WinDrop data'         -Extra @{ Profile = 'Private' }
    Set-WinDropRule -Name 'WinDrop hotspot data' -Extra @{ Profile = 'Any'; RemoteAddress = '192.168.137.0/24' }
}
