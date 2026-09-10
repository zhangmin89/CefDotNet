Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Select-TestProcesses {
    param([hashtable]$Parents, [object[]]$Seeds, [scriptblock]$ReadProcess)
    $selected = @{}
    foreach ($seed in $Seeds) {
        $current = & $ReadProcess $seed.Id
        if ($null -ne $current -and $current.StartTicks -eq $seed.StartTicks) { $selected[$seed.Id] = $current }
    }
    do {
        $added = $false
        foreach ($id in $Parents.Keys) {
            if ($selected.ContainsKey($id) -or !$selected.ContainsKey($Parents[$id])) { continue }
            $child = & $ReadProcess $id
            $parent = & $ReadProcess $Parents[$id]
            # ParentProcessId can refer to a previous owner of a recycled PID.
            if ($null -ne $child -and $null -ne $parent -and $parent.StartTicks -eq $selected[$Parents[$id]].StartTicks -and $child.StartTicks -ge $parent.StartTicks) {
                $selected[$id] = $child
                $added = $true
            }
        }
    } while ($added)
    $selected.Values | Sort-Object -Property Id
}
