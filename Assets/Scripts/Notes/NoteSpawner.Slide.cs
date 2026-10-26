public partial class NoteSpawner
{
    public bool HasSlideNotes
    {
        get
        {
            if (chart?.notes == null) return false;
            foreach (var note in chart.notes)
                if (LongSlideRules.Enabled(note)) return true;
            return false;
        }
    }

    void UpdateSlideNote(CuttableNote note, double songTime)
    {
        note.UpdateSlide(songTime, LingerSecondsFor(note));
    }
}
