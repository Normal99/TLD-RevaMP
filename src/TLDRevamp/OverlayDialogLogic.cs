namespace TLDRevamp
{
    internal struct JoinDialogLayout
    {
        internal float HostY, StagesY, ProgressY, DetailY, ExtraY, QuestionButtonsY, ButtonsY, Height;
        internal static JoinDialogLayout Measure(float header, float padding, float gap, float title, float host,
            float stageRow, int stages, float detail, float question, float stuck, float button)
        {
            var r = new JoinDialogLayout();
            r.HostY = title + gap;
            r.StagesY = r.HostY + host + 2 * gap;
            r.ProgressY = r.StagesY + stages * stageRow + gap;
            r.DetailY = r.ProgressY + gap / 2 + gap;
            r.ExtraY = r.DetailY + detail + gap;
            r.QuestionButtonsY = r.ExtraY + question + gap;
            r.ButtonsY = question > 0 ? r.QuestionButtonsY + button + gap : r.ExtraY + stuck + (stuck > 0 ? gap : 0);
            r.Height = header + r.ButtonsY + button + padding;
            return r;
        }
    }
}
