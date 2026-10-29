<#
.SYNOPSIS
    End-to-end smoke test: copy three strings, open the popup with the hotkey, paste the second item into a
    throwaway text box, and check what arrived.

.DESCRIPTION
    This drives the real desktop with synthetic keystrokes and overwrites the clipboard.
    Don't touch the keyboard or mouse while it runs. It aborts before sending any key unless the
    test window has focus, so keystrokes never reach another app.
    Uses the real %LOCALAPPDATA%\WinClip data folder; history from the run stays there.
#>
param(
    [string]$Exe = (Join-Path $PSScriptRoot '..\src\WinClip\bin\Debug\net10.0-windows\WinClip.exe')
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class E2E {
  [StructLayout(LayoutKind.Sequential)] public struct KI { public ushort vk, scan; public uint flags, time; public IntPtr extra; }
  [StructLayout(LayoutKind.Explicit, Size=40)] public struct IN { [FieldOffset(0)] public uint type; [FieldOffset(8)] public KI ki; }
  [DllImport("user32.dll")] static extern uint SendInput(uint n, IN[] i, int size);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string c, string t);
  static IN K(ushort vk, bool up) { var i = new IN(); i.type = 1; i.ki.vk = vk; i.ki.flags = up ? 2u : 0u; return i; }
  public static void Chord(params ushort[] vks) {
    var list = new System.Collections.Generic.List<IN>();
    foreach (var v in vks) list.Add(K(v, false));
    for (int j = vks.Length - 1; j >= 0; j--) list.Add(K(vks[j], true));
    SendInput((uint)list.Count, list.ToArray(), Marshal.SizeOf(typeof(IN)));
  }
}
'@

$work = Join-Path $env:TEMP "winclip-e2e-$PID"
New-Item -ItemType Directory -Force $work | Out-Null
$out = Join-Path $work 'out.txt'
$targetScript = Join-Path $work 'target.ps1'
@'
param([string]$OutFile)
Add-Type -AssemblyName System.Windows.Forms
$form = New-Object System.Windows.Forms.Form -Property @{ Text = 'WinClip E2E target'; Width = 700; Height = 300; TopMost = $true }
$box = New-Object System.Windows.Forms.TextBox -Property @{ Multiline = $true; Dock = 'Fill' }
$form.Controls.Add($box)
$timer = New-Object System.Windows.Forms.Timer -Property @{ Interval = 10000 }
$timer.Add_Tick({ [IO.File]::WriteAllText($OutFile, $box.Text); $form.Close() })
$form.Add_Shown({ $form.Activate(); $box.Focus(); $timer.Start() })
[void]$form.ShowDialog()
'@ | Set-Content $targetScript

$app = Start-Process $Exe -PassThru
try {
    Start-Sleep -Seconds 2
    foreach ($text in 'alpha one', "beta two`r`nsecond line", 'gamma three') {
        Set-Clipboard -Value $text
        Start-Sleep -Milliseconds 400
    }

    $target = Start-Process pwsh -ArgumentList '-NoProfile', '-File', $targetScript, '-OutFile', $out -PassThru
    $window = [IntPtr]::Zero
    for ($i = 0; $i -lt 50 -and $window -eq [IntPtr]::Zero; $i++) {
        Start-Sleep -Milliseconds 100
        $window = [E2E]::FindWindow($null, 'WinClip E2E target')
    }
    Start-Sleep -Milliseconds 800

    if ([E2E]::GetForegroundWindow() -ne $window) {
        $target.Kill()
        throw 'The test window did not get focus (Windows blocks focus stealing). Click it during the next run, or run from a focused terminal.'
    }

    [E2E]::Chord(0x11, 0x12, 0x56)   # Ctrl+Alt+V
    Start-Sleep -Milliseconds 700
    [E2E]::Chord(0x28)               # Down: "beta two"
    Start-Sleep -Milliseconds 300
    [E2E]::Chord(0x0D)               # Enter: paste
    $target.WaitForExit(20000) | Out-Null

    $pasted = Get-Content $out -Raw
    if ($pasted -ceq "beta two`r`nsecond line") { Write-Host 'PASS: second item pasted into the target'; exit 0 }
    Write-Host "FAIL: target contained [$pasted]"
    exit 1
}
finally {
    if (-not $app.HasExited) { $app.Kill() }
}
