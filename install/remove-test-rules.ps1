#Requires -RunAsAdministrator
<#
  NetProbe denemelerinde elle eklenen eski test kurallarını kaldırır. Yönetici gerektirir.
#>
$ErrorActionPreference = 'Stop'

foreach ($name in 'WinDrop test 53999', 'WinDrop test hotspot 53999') {
    $rules = Get-NetFirewallRule -DisplayName $name -ErrorAction SilentlyContinue
    if ($rules) {
        $rules | Remove-NetFirewallRule
        Write-Host "Test kuralı silindi: $name"
    } else {
        Write-Host "Kural yok (atlandı): $name"
    }
}
