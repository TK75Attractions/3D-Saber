using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

public class CutFeelAudioPlayTests
{
    GameObject root, prefab;
    NoteSpawner spawner;
    LongNoteCutSfx sound;
    readonly List<CuttableNote> notes = new List<CuttableNote>();

    [UnitySetUp] public IEnumerator Setup()
    {
        root = new GameObject("LongFeelAudio", typeof(AudioSource));
        sound = root.AddComponent<LongNoteCutSfx>(); spawner = root.AddComponent<NoteSpawner>();
        prefab = new GameObject("LongFeelAudioPrefab", typeof(CuttableNote));
        spawner.notePrefab = prefab; spawner.buildTimingCues = false; sound.Bind(spawner);
        spawner.OnNoteSpawned += note => notes.Add(note);
        var chart = new ChartData();
        chart.notes.Add(new NoteData { time = 1000, count = 4 });
        chart.notes.Add(new NoteData { time = 1000, count = 4, x = 2 });
        spawner.SetChart(chart); spawner.Tick(1);
        Assert.AreEqual(2, notes.Count);
        yield return null;
    }

    [UnityTearDown] public IEnumerator Cleanup()
    {
        foreach (var note in notes) if (note != null) Object.Destroy(note.gameObject);
        notes.Clear(); if (root != null) Object.Destroy(root); if (prefab != null) Object.Destroy(prefab);
        yield return null; yield return null;
    }

    AudioSource[] Voices() => root.GetComponentsInChildren<AudioSource>().Where(s => s.gameObject != root).ToArray();

    [UnityTest] public IEnumerator SimultaneousLongsKeepIndependentPitchAndFinalHitDoesNotPlayATick()
    {
        notes[0].Cut(Vector3.zero, Vector3.up * 8);
        notes[0].Cut(Vector3.zero, Vector3.down * 8);
        notes[1].Cut(Vector3.right * 2, Vector3.up * 8);
        var voices = Voices(); Assert.AreEqual(4, voices.Length);
        Assert.That(voices[0].pitch, Is.EqualTo(1).Within(.001f));
        Assert.Greater(voices[1].pitch, voices[0].pitch);
        Assert.That(voices[2].pitch, Is.EqualTo(1).Within(.001f), "別のロングは原音から始める");
        notes[0].Cut(Vector3.zero, Vector3.up * 8);
        var pitches = voices.Select(v => v.pitch).ToArray(); var clips = voices.Select(v => v.clip).ToArray();
        notes[0].Cut(Vector3.zero, Vector3.down * 8);
        CollectionAssert.AreEqual(pitches, voices.Select(v => v.pitch).ToArray());
        CollectionAssert.AreEqual(clips, voices.Select(v => v.clip).ToArray());
        yield return null;
    }

    [UnityTest] public IEnumerator ChartResetAndDisableStopVoicesAndRemoveOldSubscriptions()
    {
        notes[0].Cut(Vector3.zero, Vector3.up * 8);
        var voices = Voices(); Assert.True(voices.Any(v => v.clip != null));
        sound.Bind(null);
        Assert.True(voices.All(v => v.clip == null && !v.isPlaying));
        notes[0].Cut(Vector3.zero, Vector3.down * 8);
        Assert.True(voices.All(v => v.clip == null));
        sound.Bind(spawner);
        var chart = new ChartData(); chart.notes.Add(new NoteData { time = 1000, count = 3 });
        spawner.SetChart(chart); spawner.Tick(1);
        notes.Last().Cut(Vector3.zero, Vector3.up * 8);
        Assert.True(voices.Any(v => v.clip != null));
        spawner.SetChart(new ChartData());
        Assert.True(voices.All(v => v.clip == null));
        sound.enabled = false;
        Assert.True(voices.All(v => !v.isPlaying));
        yield return null;
    }

    [UnityTest] public IEnumerator DestroyingSoundComponentReleasesOnlyItsVoices()
    {
        var voices = Voices(); var master = root.GetComponent<AudioSource>();
        var bundled = sound.ClipForCut(0);
        Object.Destroy(sound); yield return null; yield return null;
        Assert.True(voices.All(v => v == null)); Assert.True(master != null); Assert.True(bundled != null);
    }
}
