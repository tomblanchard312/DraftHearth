Attribute VB_Name = "Cleanup"
Option Explicit

' Recorded with Word's macro recorder, then tidied: fix double spaces, normalise a term.
Sub FixDoubleSpaces()
    Selection.HomeKey Unit:=wdStory
    Selection.Find.ClearFormatting
    Selection.Find.Replacement.ClearFormatting
    With Selection.Find
        .Text = "  "
        .Replacement.Text = " "
        .Forward = True
        .Wrap = wdFindContinue
        .Format = False
        .MatchCase = False
        .MatchWholeWord = False
        .MatchWildcards = False
    End With
    Selection.Find.Execute Replace:=wdReplaceAll
    Selection.Find.Execute Replace:=wdReplaceAll
End Sub

Sub BoldEveryAcme()
    Dim rng As Range
    Dim count As Long
    Set rng = ActiveDocument.Content
    With rng.Find
        .Text = "Acme"
        .MatchCase = True
        Do While .Execute
            rng.Font.Bold = True
            count = count + 1
            rng.Collapse wdCollapseEnd
        Loop
    End With
    Debug.Print "Bolded "; count; " occurrences"
End Sub

Sub StyleHeadings()
    Dim p As Paragraph
    Dim i As Integer
    For Each p In ActiveDocument.Paragraphs
        i = i + 1
        If Left$(p.Range.Text, 8) = "Section " Then
            p.Style = ActiveDocument.Paragraphs(1).Style
            p.Style = wdStyleHeading1
            p.Range.ParagraphFormat.SpaceBefore = 12
        End If
    Next p
    MsgBox "Styled " & i & " paragraphs", vbInformation, "Done"
End Sub

Sub AddSignature(Optional name As String, Optional ByVal title)
    Dim r As Range, msg
    If name = "" Then name = "Tom"
    If IsMissing(title) Then title = "Engineer"
    Set r = ActiveDocument.Content
    r.InsertParagraphAfter
    r.InsertAfter "Regards," & vbCr & name & ", " & title
    msg = "Signed " & Format(DateSerial(2026, 10, 24), "mmmm d, yyyy")
    Debug.Print msg
End Sub

Sub Main()
    FixDoubleSpaces
    BoldEveryAcme
    StyleHeadings
    AddSignature
End Sub
