# Profilot - Brief לאיפיון

מסמך מסכם של מה שסוכם בשיחת הברainstorming. מטרתו לזרוע את האיפיון המלא (product -> ux-designer -> architect) שייכתב בתוך הפרויקט. זה לא האיפיון המלא - זה ה-context שממנו מתחילים.

## בקצרה (one-liner)

Copilot לפרופיילר של Unity: כלי ניטור ביצועים שיושב ברקע, תופס לבד spikes בביצועים (frame hitches, GC allocations, draw calls), ושולח את הנתונים המובנים של הפריים הבעייתי לקלוד - שמאבחן את הסיבה, מצביע על השורה בקוד שאחראית, ומציע תיקון.

## הבעיה

- לקרוא את ה-Unity Profiler / Frame Debugger ולהבין למה יש spike זו מיומנות של מפתח סניור. רוב הקהילה לא יודעת לעבוד עם הכלים האלה.
- התוצאה: מפתחים מתעלמים מבעיות ביצועים עד שמאוחר, או לא יודעים בכלל איפה להתחיל לחפש.
- אין כלי שלוקח את הידע הנדיר הזה והופך אותו לזמין לכולם.

## הקהל

- מפתחי Unity, בדגש על מתחילים-עד-בינוניים שמפחדים מאופטימיזציה.
- חופף ישירות לקהל של סדרת היוטיוב "Claude Code for Unity" - כל פיצ'ר הוא פרק פוטנציאלי.

## למה זה ויראלי + פרקטי

- דמוקרטיזציה של ידע נדיר: "צילמתי את ה-Profiler, קלוד מצא שאני מקצה garbage כל פריים ותיקן" = "וואו, גם אני יכול".
- כאב אמיתי ויומיומי, לא גימיק.
- עובד על קוד ועל נתוני profiler שזהים בכל פרויקט - אין את בעיית ה"כל משחק שונה" שהורגת רעיונות שדורשים הבנת gameplay.

## ההחלטה המכריעה: live, לא screenshot

לא צריך לתת לקלוד תמונה. Unity חושף את נתוני ה-Profiler כ-data דרך API, וזה המוצר הנכון (אין אובדן מידע, יש call stack, קל למפות לקוד).

### APIs רלוונטיים

- `ProfilerRecorder` (מ-Unity 2020.2) - קריאת counters בזמן ריצה ב-overhead נמוך: frame time, GC allocated per frame, draw calls, set-pass calls, tris, batches, זיכרון. זה ה-tripwire.
- `ProfilerDriver` + `HierarchyFrameDataView` / `RawFrameDataView` (צד Editor) - שליפת העץ המלא של הפריים הבעייתי: אילו markers אכלו זמן, ה-call hierarchy, כמה כל אחד הקצה.
- `FrameTimingManager` - timings של GPU/CPU.

## הארכיטקטורה (ברמת רעיון): tripwire זול + קלוד על אירוע

שתי שכבות, כדי לא לקרוא לקלוד כל פריים (יקר ומיותר):

1. ניטור live מקומי בלי LLM: `ProfilerRecorder` דוגם רצוף, היוריסטיקה פשוטה תופסת אנומליה (frame time מעל התקציב, GC alloc שקופץ מעל 0, spike ב-draw calls). רץ חינם ברקע.
2. על trip: שליפת הפריים המלא דרך `ProfilerDriver`, צירוף הקוד הרלוונטי מהריפו, שליחה לקלוד שמאבחן ומציע fix.

כך עלות ה-LLM היא רק על אירועים אמיתיים, וזה הופך אותו לכלי שאפשר להשאיר דולק.

## אזהרה שחשוב לדעת מראש

ה-markers של ה-Profiler זמינים ב-Play Mode ב-Editor וב-development builds בלבד. ב-release build הם נחתכים. זה בסדר למקרה השימוש (מפתחים מפתחים ב-Editor), רק לא להפתיע.

## התקדמות מומלצת (גם אסטרטגיית תוכן)

- קליפ 1 / MVP-דמו: screenshot של ה-Profiler -> קלוד מאבחן. אפס אינטגרציה, מוכיח ערך ב-5 דקות, ויראלי, מזין פוסט.
- המוצר האמיתי: ה-tripwire ה-live שתופס לבד וקורא לקלוד עם data מובנה. זה ה"שומר ביצועים שיושב בעורף" - וזה מה שמתקינים.

## השם

**Profilot** (Profiler + pilot/copilot).

- npm: פנוי. אין מוצר תוכנה קיים בשם הזה (לא להתבלבל עם ProfileTool / Profil Software).
- ה-username profilot ב-GitHub תפוס (חשבון רדום) אך לא חוסם - אפשר org/repo בנוסח profilot-dev / getprofilot.
- נבחנו ונפסלו: Pulse (תפוס בכבדות, שחוק בעולם monitoring), Sentinel (התנגשות חזיתית עם HashiCorp Sentinel ו-Microsoft Sentinel ב-VS Code Marketplace).
- לבדוק עוד לפני התחייבות סופית: דומיין (profilot.dev / profilot.app) ו-publisher handle ב-VS Code Marketplace.

## שאלות פתוחות לאיפיון המלא

- צורת האריזה: אקסטנשן ל-VS Code? חבילת Unity (UPM)? MCP server? שילוב? (משנה הרבה בעיצוב)
- אילו spikes בדיוק שווה לתפוס ובאילו ספים (frame time budget, GC, draw calls, physics)?
- איך בדיוק קלוד מקבל את ה-context של הקוד (גישה לריפו, מיפוי marker -> קובץ/שורה)?
- איפה רץ ה-tripwire ואיך הוא מתקשר עם הצד שמדבר עם קלוד.
- מודל עלות ה-LLM וניהול תדירות הקריאות.

## מה להעביר ל-pipeline האיפיון המלא

- product (סעיפים 1-6): הבעיה, הקהל, goals/non-goals, user stories, scope, מדדי הצלחה. דגש: MVP-דמו מול המוצר ה-live.
- ux-designer (סעיפים 7-11): איך נראית התראת spike, איך מוצג האבחון והתיקון, מצבים (אין בעיות / spike נתפס / מחכה לקלוד / הוצע fix), היכן זה חי בעין המשתמש.
- architect (סעיפים 12-17): הארכיטקטורה הדו-שכבתית, בחירת ה-APIs, צורת האריזה, NFRs (overhead הניטור, עלות LLM), roadmap מ-דמו ל-live.
