using System;
using Autodesk.AutoCAD.ApplicationServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AcadMcp.Acad
{
    internal static class AcadContext
    {
        /// <summary>当前活动文档。无打开图形时抛出面向用户的异常。只应在主线程访问。</summary>
        public static Document ActiveDocument
        {
            get
            {
                var doc = AcApp.DocumentManager.MdiActiveDocument;
                if (doc == null)
                    throw new InvalidOperationException("AutoCAD 当前没有打开的图形。请先新建或打开一个 DWG。");
                return doc;
            }
        }
    }
}
