Option Explicit
Dim fs, shell, root, status
Set fs = CreateObject("Scripting.FileSystemObject")
Set shell = CreateObject("WScript.Shell")
root = fs.GetParentFolderName(WScript.ScriptFullName)
shell.CurrentDirectory = root
status = shell.Run("""" & fs.BuildPath(root, "UvncFtpBridge.exe") & """ winscp", 0, True)
WScript.Quit status
