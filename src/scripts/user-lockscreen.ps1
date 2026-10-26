# Per-user lock-screen picture = the sky (run as the user, not elevated).
# The machine-wide picture (PersonalizationCSP, registry\lockscreen-sky.reg) covers only the first, locked screen. The
# sign-in screen (after a click on the account picture) shows the USER's own lock-screen picture; with none set it stayed
# black (Viksa, 26.10). Windows keeps that picture per user and sets it only through WinRT LockScreen.SetImageFileAsync.
param([string]$Image = 'C:\ProgramData\fanatic\lockscreen-sky.png')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$asTask = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' } | Select-Object -First 1
function Await($op, [Type]$t) { $task = $asTask.MakeGenericMethod($t).Invoke($null, @($op)); $task.Wait(-1) | Out-Null; $task.Result }
function AwaitAction($op) { $m = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncAction' } | Select-Object -First 1; $m.Invoke($null, @($op)).Wait(-1) | Out-Null }
[void][Windows.Storage.StorageFile, Windows.Storage, ContentType = WindowsRuntime]
[void][Windows.System.UserProfile.LockScreen, Windows.System.UserProfile, ContentType = WindowsRuntime]
$file = Await ([Windows.Storage.StorageFile]::GetFileFromPathAsync($Image)) ([Windows.Storage.StorageFile])
AwaitAction ([Windows.System.UserProfile.LockScreen]::SetImageFileAsync($file))
'user lock screen: ' + $Image
