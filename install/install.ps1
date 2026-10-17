#Requires -RunAsAdministrator
<#
  WinDrop güvenlik duvarı kuralları (PROTOCOL §5). Yönetici gerektirir; yeniden çalıştırmak güvenlidir (kurallar yeniden oluşturulur).
    "WinDrop data"          : TCP 53600 gelen, yalnız Private profil
    "WinDrop hotspot data"  : TCP 53600 gelen, tüm profiller, yalnız RemoteAddress 192.168.137.0/24
                              (Mobil etkin nokta arayüzünün ağ profili yok; Windows onu "Genel" gibi sayar)
#>
[CmdletBinding()]
param(
    [int]$Port = 53600
)
$ErrorActionPreference = 'Stop'

function Set-WinDropRule {
    param([string]$Name, [hashtable]$Extra)
    Get-NetFirewallRule -DisplayName $Name -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    New-NetFirewallRule -DisplayName $Name -Group 'WinDrop' -Direction Inbound -Action Allow `
        -Protocol TCP -LocalPort $Port -Enabled True @Extra | Out-Null
    Write-Host "Kural eklendi: $Name"
}

Set-WinDropRule -Name 'WinDrop data'         -Extra @{ Profile = 'Private' }
Set-WinDropRule -Name 'WinDrop hotspot data' -Extra @{ Profile = 'Any'; RemoteAddress = '192.168.137.0/24' }

Write-Host 'Tamam. Geri almak için: install\uninstall.ps1'
