<#
.SYNOPSIS
    Starts PasteJump elevated at logon, through a scheduled task.

.DESCRIPTION
    There is one situation where PasteJump must run elevated to work at all, and it is not a preference:
    endpoint security software can route a particular application's keyboard input through a component of
    higher integrity than PasteJump, and Windows then excludes PasteJump's low-level hook from seeing that
    input. UIPI, working as designed. The symptom is Ctrl+V doing nothing in one application - a browser,
    usually - while every other application is fine.

    Measured on 2026-08-21: an elevated hook received Alt+Tab pressed inside the affected application at the
    same moments PasteJump's medium-integrity hook received nothing, and launching PasteJump as administrator
    restored the gesture there immediately.

    A shortcut cannot do this. Elevation cannot be requested by a .lnk without a UAC prompt every time, which
    is no way to start a logon-resident application - so the mechanism is a scheduled task with the highest
    privileges, triggered at logon. That is also how AltTab.NET runs on the machine this was written for.

    The task replaces the ordinary "Run at logon" shortcut rather than joining it: two of them would start two
    copies, and the second would find the first through the single-instance mutex and merely surface it.

.PARAMETER ExePath
    The PasteJump.exe to run. Defaults to the development deployment.

.PARAMETER TaskName
    Name of the scheduled task.

.PARAMETER Remove
    Delete the task and stop starting PasteJump elevated at logon.

.EXAMPLE
    # From an ELEVATED PowerShell prompt:
    powershell -ExecutionPolicy Bypass -File tools\install-elevated-task.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\install-elevated-task.ps1 -Remove
#>
[CmdletBinding()]
param(
    [string] $ExePath = 'D:\Lokesh\DoNotMove\PasteJump\PasteJump.exe',
    [string] $TaskName = 'PasteJump (elevated)',
    [switch] $Remove
)

$ErrorActionPreference = 'Stop'

# Creating a task with the highest privileges needs them, and the failure without them is an opaque
# "Access is denied" from schtasks - so it is checked up front and named.
$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()

if (-not (New-Object System.Security.Principal.WindowsPrincipal($identity)).IsInRole(
        [System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "This script has to run elevated. Open PowerShell with 'Run as administrator' and try again."
}

if ($Remove) {
    if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
        Write-Host "Removed the task '$TaskName'." -ForegroundColor Green
        Write-Host "PasteJump will no longer start elevated at logon. Re-enable 'Run at logon' in Settings if"
        Write-Host "you want the ordinary, non-elevated shortcut back."
    }
    else {
        Write-Host "No task named '$TaskName' - nothing to remove."
    }

    return
}

if (-not (Test-Path -LiteralPath $ExePath)) {
    throw "No PasteJump.exe at '$ExePath'. Pass -ExePath with the location of your deployment."
}

# RunLevel Highest is the whole point of the exercise. LogonType Interactive because the application draws
# windows on the desktop and must be in the user's session, not in session 0.
$action    = New-ScheduledTaskAction  -Execute $ExePath
$trigger   = New-ScheduledTaskTrigger -AtLogOn -User $identity.Name
$principal = New-ScheduledTaskPrincipal -UserId $identity.Name -RunLevel Highest -LogonType Interactive

# ExecutionTimeLimit zero means "do not stop it after a while", which matters for something meant to run all
# day; StartWhenAvailable keeps a missed logon trigger from being dropped silently. Battery settings are off
# by default in a task and would otherwise refuse to start on an unplugged laptop.
$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -ExecutionTimeLimit ([TimeSpan]::Zero)

Register-ScheduledTask `
    -TaskName $TaskName `
    -Action $action `
    -Trigger $trigger `
    -Principal $principal `
    -Settings $settings `
    -Description 'Starts PasteJump with administrator rights so its keyboard hook is not excluded by UIPI where endpoint security intercepts input at a higher integrity level.' `
    -Force | Out-Null

Write-Host "Registered '$TaskName' -> $ExePath (highest privileges, at logon)." -ForegroundColor Green
Write-Host ''
Write-Host 'Two things to do now:'
Write-Host '  1. Turn OFF "Run at logon" in PasteJump Settings, System - otherwise two copies start and the'
Write-Host '     second only surfaces the first through the single-instance mutex.'
Write-Host '  2. Exit the running copy from the tray, then start it from this task:'
Write-Host "       schtasks /Run /TN `"$TaskName`""
Write-Host ''
Write-Host 'To check it took effect, the tray tooltip is unchanged - but the gesture will work in the'
Write-Host 'application that was ignoring it, and logs\gesture.log will start recording keys with that'
Write-Host 'application in the foreground.'
