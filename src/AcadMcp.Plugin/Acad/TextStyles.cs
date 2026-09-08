using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.GraphicsInterface;

namespace AcadMcp.Acad
{
    internal static class TextStyles
    {
        private const string StyleName = "MCP";

        /// <summary>
        /// 返回一个能显示中文的文字样式 ObjectId（基于 Windows 宋体 simsun.ttc）。
        /// 若创建失败（字体缺失等），退回当前文字样式，保证 draw_text 不因样式问题失败。
        /// </summary>
        public static ObjectId EnsureUnicodeStyle(Transaction tr, Database db)
        {
            try
            {
                var stt = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
                if (stt.Has(StyleName))
                    return stt[StyleName];

                stt.UpgradeOpen();
                var rec = new TextStyleTableRecord
                {
                    Name = StyleName,
                    FileName = "simsun.ttc",
                    Font = new FontDescriptor("SimSun", false, false, 0, 0),
                    TextSize = 0.0,   // 0 = 由每个 DBText 自己的 Height 决定
                };
                var id = stt.Add(rec);
                tr.AddNewlyCreatedDBObject(rec, true);
                return id;
            }
            catch
            {
                return db.Textstyle;
            }
        }
    }
}
