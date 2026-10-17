#Requires -RunAsAdministrator
<#
  install.ps1'in eklediği güvenlik duvarı kurallarını kaldırır. Yönetici gerektirir.
#>
$ErrorActionPreference = 'Stop'

foreach ($name in 'WinDrop data', 'WinDrop hotspot data') {
    $rules = Get-NetFirewallRule -DisplayName $name -ErrorAction SilentlyContinue
    if ($rules) {
        $rules | Remove-NetFirewallRule
        Write-Host "Kural silindi: $name"
    } else {
        Write-Host "Kural yok (atlandı): $name"
    }
}
