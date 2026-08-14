' Reads the folder picked by FolderPicker.exe (HKCU\Software\dsh-launcher\InstallDir)
' and applies it to the wizard's internal browse property (_BrowseProperty).
' Runs as an immediate-context custom action from the Browse button's event chain;
' custom actions DO re-run on every DoAction (unlike the standard AppSearch action,
' which MSI schedules only once in the UI sequence).
On Error Resume Next
Dim shell, path
Set shell = CreateObject("WScript.Shell")
path = shell.RegRead("HKCU\Software\dsh-launcher\InstallDir\")
If Err.Number = 0 And Len(path) > 0 Then
  Session.Property("_BrowseProperty") = path
  Session.Property("PICKED_OK") = "1"
End If
