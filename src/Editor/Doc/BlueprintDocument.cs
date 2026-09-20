using System;
using System.Collections.Generic;
using UnityEngine;
using ValheimTomrer.Blueprints;

namespace ValheimTomrer.Editor.Doc
{
    /// <summary>
    /// One piece while it is being edited. Never changed in place: an edit makes a new one with the
    /// same id, so the old snapshot stays valid on the undo stack.
    /// </summary>
    internal sealed class DocPiece
    {
        public readonly int Id;
        public readonly string PrefabName;
        public readonly string Category;
        public readonly Vector3 Position;
        public readonly Quaternion Rotation;
        public readonly string Rest;

        public DocPiece(int id, string prefabName, string category, Vector3 position, Quaternion rotation, string rest)
        {
            Id = id;
            PrefabName = prefabName;
            Category = category ?? "";
            Position = position;
            Rotation = rotation;
            Rest = rest;
        }

        public DocPiece Moved(Vector3 position)
        {
            return new DocPiece(Id, PrefabName, Category, position, Rotation, Rest);
        }

        public DocPiece Turned(Quaternion rotation)
        {
            return new DocPiece(Id, PrefabName, Category, Position, rotation, Rest);
        }

        public DocPiece Placed(Vector3 position, Quaternion rotation)
        {
            return new DocPiece(Id, PrefabName, Category, position, rotation, Rest);
        }
    }

    /// <summary>
    /// A blueprint open in the editor. Nothing is changed in place: every edit builds a new snapshot
    /// and pushes the old one on the undo stack. A run of keystrokes in one field is one step.
    /// </summary>
    internal sealed class BlueprintDocument
    {
        /// <summary>How many steps back the editor can go. Older ones fall off the bottom.</summary>
        public const int UndoLimit = 200;

        private static int _lastId;

        private readonly List<Snapshot> _undo = new List<Snapshot>();
        private readonly List<Snapshot> _redo = new List<Snapshot>();
        private Snapshot _now;
        private Snapshot _saved;
        private string _lastTag;

        private BlueprintDocument(Snapshot now)
        {
            _now = now;
            _saved = now;
        }

        /// <summary>The file it came from, or null for a kit or a blueprint that was never saved.</summary>
        public string SourcePath { get; private set; }

        /// <summary>Can only be saved under a new name: a kit, a .vbuild, or a file with sections.</summary>
        public bool ReadOnly { get; private set; }

        /// <summary>The file had a section we cannot write back.</summary>
        public bool HasSections { get; private set; }

        /// <summary>Changed since the last save.</summary>
        public bool Dirty => !ReferenceEquals(_now, _saved);

        /// <summary>Counts up on every change, so the 3D pane can tell it has to rebuild.</summary>
        public int Revision { get; private set; }

        public string Name => _now.Name;

        public string Description => _now.Description;

        public string IconPrefab => _now.IconPrefab;

        public IReadOnlyList<DocPiece> Pieces => _now.Pieces;

        public int UndoDepth => _undo.Count;

        public bool CanUndo => _undo.Count > 0;

        public bool CanRedo => _redo.Count > 0;

        /// <summary>An empty blueprint, not on disk yet.</summary>
        public static BlueprintDocument New(string name = "New blueprint")
        {
            return new BlueprintDocument(new Snapshot
            {
                Name = name ?? "",
                Description = "",
                IconPrefab = null,
                ExtraHeaders = new List<string>(),
                Pieces = new List<DocPiece>(),
            });
        }

        /// <summary>Opens a parsed blueprint for editing.</summary>
        public static BlueprintDocument FromBlueprint(Blueprint blueprint)
        {
            var pieces = new List<DocPiece>(blueprint.Pieces.Count);
            foreach (var piece in blueprint.Pieces)
            {
                pieces.Add(new DocPiece(
                    ++_lastId, piece.PrefabName, piece.Category, piece.Position, piece.Rotation, piece.Rest));
            }

            return new BlueprintDocument(new Snapshot
            {
                Name = blueprint.Name ?? "",
                Description = blueprint.Description ?? "",
                IconPrefab = blueprint.IconPrefab,
                ExtraHeaders = new List<string>(blueprint.ExtraHeaders),
                Pieces = pieces,
            })
            {
                SourcePath = blueprint.SourcePath,
                ReadOnly = blueprint.ReadOnly,
                HasSections = blueprint.HasSections,
            };
        }

        /// <summary>What the writer and the 3D pane take.</summary>
        public Blueprint ToBlueprint()
        {
            var blueprint = new Blueprint
            {
                Name = _now.Name,
                Description = _now.Description ?? "",
                IconPrefab = _now.IconPrefab,
                SourcePath = SourcePath,
                ReadOnly = ReadOnly,
                HasSections = HasSections,
            };
            blueprint.ExtraHeaders.AddRange(_now.ExtraHeaders);
            foreach (var piece in _now.Pieces)
            {
                blueprint.Pieces.Add(new BlueprintPiece
                {
                    PrefabName = piece.PrefabName,
                    Category = piece.Category,
                    Position = piece.Position,
                    Rotation = piece.Rotation,
                    Rest = piece.Rest,
                });
            }

            return blueprint;
        }

        public DocPiece Find(int id)
        {
            foreach (var piece in _now.Pieces)
            {
                if (piece.Id == id)
                {
                    return piece;
                }
            }

            return null;
        }

        // ---------- edits ----------

        public void SetName(string value)
        {
            Change("name", s => s.Name = value ?? "");
        }

        public void SetDescription(string value)
        {
            Change("description", s => s.Description = value ?? "");
        }

        public void SetIcon(string prefabName)
        {
            Change("icon", s => s.IconPrefab = string.IsNullOrEmpty(prefabName) ? null : prefabName);
        }

        /// <summary>Adds a piece and gives back its id.</summary>
        public int AddPiece(string prefabName, Vector3 position, Quaternion rotation)
        {
            var id = ++_lastId;
            Change(null, s => s.Pieces.Add(new DocPiece(id, prefabName, "", position, rotation, null)));
            return id;
        }

        public bool RemovePieces(ICollection<int> ids)
        {
            if (ids == null || ids.Count == 0)
            {
                return false;
            }

            var hit = false;
            foreach (var piece in _now.Pieces)
            {
                if (ids.Contains(piece.Id))
                {
                    hit = true;
                    break;
                }
            }

            if (!hit)
            {
                return false;
            }

            Change(null, s => s.Pieces.RemoveAll(p => ids.Contains(p.Id)));
            return true;
        }

        /// <summary>Puts one piece somewhere else. <paramref name="tag"/> coalesces a drag into one step.</summary>
        public bool SetPiece(int id, Vector3 position, Quaternion rotation, string tag = null)
        {
            if (Find(id) == null)
            {
                return false;
            }

            Change(tag, s =>
            {
                for (var i = 0; i < s.Pieces.Count; i++)
                {
                    if (s.Pieces[i].Id == id)
                    {
                        s.Pieces[i] = s.Pieces[i].Placed(position, rotation);
                        return;
                    }
                }
            });
            return true;
        }

        // ---------- history ----------

        public bool Undo()
        {
            if (_undo.Count == 0)
            {
                return false;
            }

            var back = _undo[_undo.Count - 1];
            _undo.RemoveAt(_undo.Count - 1);
            _redo.Add(_now);
            _now = back;
            _lastTag = null;
            Revision++;
            return true;
        }

        public bool Redo()
        {
            if (_redo.Count == 0)
            {
                return false;
            }

            var forward = _redo[_redo.Count - 1];
            _redo.RemoveAt(_redo.Count - 1);
            _undo.Add(_now);
            _now = forward;
            _lastTag = null;
            Revision++;
            return true;
        }

        /// <summary>The file on disk now matches the document.</summary>
        public void MarkSaved()
        {
            _saved = _now;
        }

        /// <summary>
        /// After a save under a new name. Not an edit, so it makes no undo step and leaves the
        /// document clean.
        /// </summary>
        public void Adopt(string name, string path, bool readOnly)
        {
            var next = _now.Copy();
            next.Name = name ?? "";
            _now = next;
            _saved = next;
            SourcePath = path;
            ReadOnly = readOnly;
            HasSections = false;
            _lastTag = null;
            Revision++;
        }

        private void Change(string tag, Action<Snapshot> edit)
        {
            var before = _now;
            var next = before.Copy();
            edit(next);

            // Repeated typing in one field is one step: only the first keystroke pushes.
            if (tag == null || tag != _lastTag || _undo.Count == 0)
            {
                _undo.Add(before);
                if (_undo.Count > UndoLimit)
                {
                    _undo.RemoveAt(0);
                }
            }

            _lastTag = tag;
            _redo.Clear();
            _now = next;
            Revision++;
        }

        /// <summary>One state of the document. Replaced whole, never patched.</summary>
        private sealed class Snapshot
        {
            public string Name;
            public string Description;
            public string IconPrefab;
            public List<string> ExtraHeaders;
            public List<DocPiece> Pieces;

            public Snapshot Copy()
            {
                return new Snapshot
                {
                    Name = Name,
                    Description = Description,
                    IconPrefab = IconPrefab,
                    ExtraHeaders = new List<string>(ExtraHeaders),
                    Pieces = new List<DocPiece>(Pieces),
                };
            }
        }
    }
}
