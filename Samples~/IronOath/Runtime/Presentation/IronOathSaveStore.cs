using System;
using System.IO;
using System.Text;
using IronOath.Campaign;
using IronOath.Combat;

namespace IronOath.Presentation
{
    // Field names and version retain compatibility with the original demo's v1 JSON files.
    [Serializable]
    internal sealed class IronOathSaveData
    {
        public int Version = 1;
        public CampaignSave Campaign;
        public bool HasCustomer, HasPendingOutcome;
        public int[] Formation;
        public IronOathSavedTactic[] Tactics;
    }

    [Serializable]
    internal sealed class IronOathSavedTactic
    {
        public int Unit, Row;
        public BattleSkillId Skill;
        public TacticCondition Condition;
        public TargetPriority Priority;
        public bool Enabled;
    }

    internal sealed class IronOathLoadedSave
    {
        internal CampaignSession Campaign;
        internal int[] Formation;
        internal TacticRule[][] Tactics;
        internal bool RecoveredFromBackup;
    }

    /// <summary>Validates the complete profile before replacing either the live session or its save.</summary>
    internal sealed partial class IronOathSaveStore
    {
        internal const string FileName = "iron-oath-campaign-v1.json";
        const int MaximumBytes = 1024 * 1024;
        readonly string _directory;
        readonly Func<IronOathSaveData, string> _serialize;
        readonly Func<string, IronOathSaveData> _deserialize;
        internal string MainPath { get; }
        internal string BackupPath => MainPath + ".bak";

        internal IronOathSaveStore(string directory, Func<IronOathSaveData, string> serialize,
            Func<string, IronOathSaveData> deserialize)
        {
            if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("A save directory is required.", nameof(directory));
            _serialize = serialize ?? throw new ArgumentNullException(nameof(serialize));
            _deserialize = deserialize ?? throw new ArgumentNullException(nameof(deserialize));
            _directory = Path.GetFullPath(directory);
            MainPath = Path.Combine(_directory, FileName);
        }

        internal bool TrySave(CampaignSession campaign, int[] formation, TacticRule[][] tactics, out string error)
        {
            error = null;
            if (campaign == null) { error = "There is no campaign to save."; return false; }
            string temporary = MainPath + ".tmp";
            try
            {
                CampaignSave snapshot = campaign.CaptureSave();
                int count = 0;
                if (tactics != null)
                {
                    if (tactics.Length != Formation.MaxUnitsPerSide) throw new InvalidDataException("Invalid party tactics.");
                    for (int unit = 0; unit < tactics.Length; unit++)
                        if (tactics[unit] != null)
                        {
                            if (tactics[unit].Length != BattleUnit.TacticCount) throw new InvalidDataException("Incomplete tactics.");
                            count += tactics[unit].Length;
                        }
                }
                var data = new IronOathSaveData
                {
                    Campaign = snapshot,
                    HasCustomer = snapshot.Customer != null,
                    HasPendingOutcome = snapshot.PendingOutcome != null,
                    Formation = formation == null ? null : (int[])formation.Clone(),
                    Tactics = new IronOathSavedTactic[count]
                };
                int savedIndex = 0;
                if (tactics != null)
                    for (int unit = 0; unit < tactics.Length; unit++)
                    {
                        if (tactics[unit] == null) continue;
                        for (int row = 0; row < BattleUnit.TacticCount; row++)
                        {
                            TacticRule rule = tactics[unit][row];
                            data.Tactics[savedIndex++] = new IronOathSavedTactic
                            {
                                Unit = unit, Row = row, Skill = rule.Skill,
                                Condition = rule.Condition, Priority = rule.Priority, Enabled = rule.Enabled
                            };
                        }
                    }
                if (!TryRestore(data, out _, out error)) return false;
                string json = _serialize(data);
                if (Encoding.UTF8.GetByteCount(json) > MaximumBytes) throw new InvalidDataException("Save exceeds the size limit.");
                Directory.CreateDirectory(_directory);
                using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, true))
                    { writer.Write(json); writer.Flush(); }
                    stream.Flush(true);
                }
                if (File.Exists(MainPath))
                {
                    // A recovered session must never rotate a corrupt primary over the last good backup.
                    bool validPrimary = TryRead(MainPath, out _, out _);
                    File.Replace(temporary, MainPath, validPrimary ? BackupPath : null, true);
                }
                else File.Move(temporary, MainPath);
                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        internal bool TryLoad(out IronOathLoadedSave loaded, out string error)
        {
            if (TryRead(MainPath, out loaded, out string primaryError)) { error = null; return true; }
            if (TryRead(BackupPath, out loaded, out string backupError))
            {
                loaded.RecoveredFromBackup = true;
                error = null;
                return true;
            }
            error = primaryError ?? backupError;
            return false;
        }

        bool TryRead(string path, out IronOathLoadedSave loaded, out string error)
        {
            loaded = null;
            error = null;
            try
            {
                if (!File.Exists(path)) return false;
                if (new FileInfo(path).Length > MaximumBytes) throw new InvalidDataException("Save exceeds the size limit.");
                return TryRestore(_deserialize(File.ReadAllText(path)), out loaded, out error);
            }
            catch (Exception exception) { error = exception.Message; return false; }
        }

        static bool TryRestore(IronOathSaveData data, out IronOathLoadedSave loaded, out string error)
        {
            loaded = null;
            error = "Invalid campaign profile.";
            if (data == null || data.Version != 1 || data.Campaign == null) return false;
            // JsonUtility can materialize an inline default object for a null serializable reference.
            if (!data.HasCustomer) data.Campaign.Customer = null;
            if (!data.HasPendingOutcome) data.Campaign.PendingOutcome = null;
            if (!CampaignSession.TryRestore(data.Campaign, out CampaignSession campaign, out error)) return false;
            error = "Invalid formation or tactics.";
            int[] formation = data.Formation;
            if (formation != null && formation.Length == 0) formation = null;
            if (formation != null)
            {
                if (formation.Length != Formation.MaxUnitsPerSide) return false;
                int occupied = 0;
                for (int i = 0; i < formation.Length; i++)
                {
                    if (!Formation.IsValid(formation[i]) || (occupied & (1 << formation[i])) != 0) return false;
                    occupied |= 1 << formation[i];
                }
            }
            var tactics = new TacticRule[Formation.MaxUnitsPerSide][];
            if (data.Tactics != null && data.Tactics.Length > 0)
            {
                if (data.Tactics.Length > Formation.MaxUnitsPerSide * BattleUnit.TacticCount) return false;
                var masks = new int[Formation.MaxUnitsPerSide];
                BattleSimulation validation = BattleCatalog.CreateDemo(campaign.Protagonist == Protagonist.Chloe);
                for (int i = 0; i < data.Tactics.Length; i++)
                {
                    IronOathSavedTactic row = data.Tactics[i];
                    if (row == null || (uint)row.Unit >= Formation.MaxUnitsPerSide ||
                        (uint)row.Row >= BattleUnit.TacticCount || !Enum.IsDefined(typeof(BattleSkillId), row.Skill) ||
                        (masks[row.Unit] & (1 << row.Row)) != 0) return false;
                    var rule = new TacticRule(row.Skill, row.Condition, row.Priority, row.Enabled);
                    if (!validation.SetTactic(row.Unit, row.Row, rule)) return false;
                    if (tactics[row.Unit] == null) tactics[row.Unit] = new TacticRule[BattleUnit.TacticCount];
                    tactics[row.Unit][row.Row] = rule;
                    masks[row.Unit] |= 1 << row.Row;
                }
                int completeMask = (1 << BattleUnit.TacticCount) - 1;
                for (int i = 0; i < masks.Length; i++)
                    if (masks[i] != 0 && masks[i] != completeMask) return false;
            }
            loaded = new IronOathLoadedSave
            {
                Campaign = campaign,
                Formation = formation == null ? null : (int[])formation.Clone(),
                Tactics = tactics
            };
            error = null;
            return true;
        }
    }
}
