namespace WordCraft.ObjectModel;

/// <summary>Word enumeration values macros use as plain numbers. Imported, so macros see them unqualified.</summary>
public static class WdConstants
{
    public const int wdUndefined = 9999999;
    public const int wdToggle = 9999998;

    // WdCollapseDirection
    public const int wdCollapseEnd = 0;
    public const int wdCollapseStart = 1;

    // WdUnits
    public const int wdCharacter = 1;
    public const int wdWord = 2;
    public const int wdSentence = 3;
    public const int wdParagraph = 4;
    public const int wdLine = 5;
    public const int wdStory = 6;
    public const int wdScreen = 7;
    public const int wdSection = 8;
    public const int wdCell = 12;
    public const int wdTable = 15;

    // WdMovementType
    public const int wdMove = 0;
    public const int wdExtend = 1;

    // WdReplace / WdFindWrap
    public const int wdReplaceNone = 0;
    public const int wdReplaceOne = 1;
    public const int wdReplaceAll = 2;
    public const int wdFindStop = 0;
    public const int wdFindContinue = 1;
    public const int wdFindAsk = 2;

    // WdParagraphAlignment
    public const int wdAlignParagraphLeft = 0;
    public const int wdAlignParagraphCenter = 1;
    public const int wdAlignParagraphRight = 2;
    public const int wdAlignParagraphJustify = 3;
    public const int wdAlignParagraphDistribute = 4;

    // WdLineSpacing
    public const int wdLineSpaceSingle = 0;
    public const int wdLineSpace1pt5 = 1;
    public const int wdLineSpaceDouble = 2;
    public const int wdLineSpaceAtLeast = 3;
    public const int wdLineSpaceExactly = 4;
    public const int wdLineSpaceMultiple = 5;

    // WdUnderline
    public const int wdUnderlineNone = 0;
    public const int wdUnderlineSingle = 1;
    public const int wdUnderlineWords = 2;
    public const int wdUnderlineDouble = 3;
    public const int wdUnderlineDotted = 4;
    public const int wdUnderlineThick = 6;
    public const int wdUnderlineDash = 7;
    public const int wdUnderlineDotDash = 9;
    public const int wdUnderlineDotDotDash = 10;
    public const int wdUnderlineWavy = 11;

    // WdColor (BGR)
    public const int wdColorAutomatic = -16777216;
    public const int wdColorBlack = 0;
    public const int wdColorWhite = 16777215;
    public const int wdColorRed = 255;
    public const int wdColorGreen = 32768;
    public const int wdColorBlue = 16711680;
    public const int wdColorYellow = 65535;
    public const int wdColorGray50 = 8421504;

    // WdBuiltinStyle
    public const int wdStyleNormal = -1;
    public const int wdStyleHeading1 = -2;
    public const int wdStyleHeading2 = -3;
    public const int wdStyleHeading3 = -4;
    public const int wdStyleHeading4 = -5;
    public const int wdStyleHeading5 = -6;
    public const int wdStyleHeading6 = -7;
    public const int wdStyleHeading7 = -8;
    public const int wdStyleHeading8 = -9;
    public const int wdStyleHeading9 = -10;
    public const int wdStyleCaption = -35;
    public const int wdStyleListBullet = -49;
    public const int wdStyleListNumber = -50;
    public const int wdStyleTitle = -63;
    public const int wdStyleBodyText = -67;
    public const int wdStyleSubtitle = -75;
    public const int wdStyleListParagraph = -180;
    public const int wdStyleQuote = -181;

    // WdSaveOptions / WdAlertLevel / WdBreakType
    public const int wdDoNotSaveChanges = 0;
    public const int wdSaveChanges = -1;
    public const int wdPromptToSaveChanges = -2;
    public const int wdAlertsNone = 0;
    public const int wdAlertsAll = -1;
    public const int wdPageBreak = 7;
}
