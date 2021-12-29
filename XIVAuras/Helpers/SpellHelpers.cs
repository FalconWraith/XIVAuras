﻿using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Data;
using Dalamud.Game.ClientState;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel;
using Lumina.Excel.GeneratedSheets;
using XIVAuras.Config;
using LuminaAction = Lumina.Excel.GeneratedSheets.Action;
using LuminaStatus = Lumina.Excel.GeneratedSheets.Status;

namespace XIVAuras.Helpers
{
    public class SpellHelpers
    {
        private const string _pathToTargetClearSig = "E8 ?? ?? ?? ?? 3C 01 75 5D";

        private readonly unsafe ActionManager* _actionManager;

        public unsafe SpellHelpers()
        {
            _actionManager = ActionManager.Instance();
        }

        public unsafe uint GetSpellActionId(uint actionId)
        {
            return _actionManager->GetAdjustedActionId(actionId);
        }

        public unsafe float GetRecastTimeElapsed(uint actionId)
        {
            int recastGroup = _actionManager->GetRecastGroup((int)ActionType.Spell, this.GetSpellActionId(actionId));
            RecastDetail* recastDetail = _actionManager->GetRecastGroupDetail(recastGroup);
            if (recastDetail == null)
                return 0f;
            
            return recastDetail->Elapsed;
        }

        public unsafe float GetRecastTime(uint actionId)
        {
            int recastGroup = _actionManager->GetRecastGroup((int)ActionType.Spell, this.GetSpellActionId(actionId));
            RecastDetail* recastDetail = _actionManager->GetRecastGroupDetail(recastGroup);
            if (recastDetail == null)
                return 0f;
            
            return recastDetail->Total;
        }

        public float GetAdjustedRecastTime(uint actionId)
        {
            float totalRecastTime = this.GetRecastTime(actionId);
            int maxCharges = this.GetMaxCharges(actionId, 90);
            if (maxCharges <= 1)
                return totalRecastTime;

            int myMaxCharges = this.GetMaxCharges(actionId, 0);
            return (totalRecastTime * myMaxCharges) / maxCharges;
        }

        public unsafe (float, float, int) GetAdjustedRecastInfo(uint actionId)
        {
            int recastGroup = _actionManager->GetRecastGroup((int)ActionType.Spell, actionId);
            RecastDetail* recastDetail = _actionManager->GetRecastGroupDetail(recastGroup);
            if (recastDetail == null)
                return (0, 0, 0);
            
            float recast = recastDetail->Total;
            float elapsed = recastDetail->Elapsed;
            int maxCharges = this.GetMaxCharges(actionId, 90);
            if (maxCharges <= 1)
                return (recast, elapsed, maxCharges);

            int myMaxCharges = this.GetMaxCharges(actionId, 0);
            float adjustedRecast = (recast * myMaxCharges) / maxCharges;
            if (elapsed > adjustedRecast)
                return (0, 0, myMaxCharges);

            return (adjustedRecast, elapsed, myMaxCharges);
        }

        public unsafe uint GetActionStatus(uint actionId, uint targetId = 0xE000_0000)
        {
            return _actionManager->GetActionStatus(ActionType.Spell, GetSpellActionId(actionId), targetId);
        }

        public unsafe bool CanUseAction(uint actionId, uint targetId = 0xE000_0000)
        {
            return _actionManager->GetActionStatus(ActionType.Spell, GetSpellActionId(actionId), targetId, 0, 1) == 0;
        }

        public unsafe bool GetActionInRange(uint actionId, GameObject? player, GameObject? target)
        {
            if (player is null || target is null)
                return false;

            uint result = ActionManager.GetActionInRangeOrLoS(
                actionId,
                (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)player.Address,
                (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)target.Address);

            Dalamud.Logging.PluginLog.Information($"{result}");

            return result != 566; // 0 == in range, 565 == in range but not facing target, 566 == out of range, 562 == not in LoS
        }

        public ushort GetMaxCharges(uint actionId, uint level = 0)
        {
            return ActionManager.GetMaxCharges(GetSpellActionId(actionId), level);
        }

        public static DataSource GetStatusData(TriggerSource source, IEnumerable<TriggerData> triggerData, bool onlyMine, bool preview)
        {
            if (preview)
            {
                return new DataSource()
                {
                    Active = true,
                    Value = 10,
                    Stacks = 2,
                    MaxStacks = 2,
                    Icon = triggerData.FirstOrDefault().Icon
                };
            }

            PlayerCharacter? player = Singletons.Get<ClientState>().LocalPlayer;
            if (player is null)
            {
                return new DataSource();
            }

            GameObject? actor = source switch
            {
                TriggerSource.Player => player,
                TriggerSource.Target => Utils.FindTarget(),
                TriggerSource.TargetOfTarget => Utils.FindTargetOfTarget(player),
                TriggerSource.FocusTarget => Singletons.Get<TargetManager>().FocusTarget,
                _ => null
            };

            if (actor is not BattleChara chara)
            {
                return new DataSource();
            }

            foreach (TriggerData trigger in triggerData)
            {
                foreach (var status in chara.StatusList)
                {
                    if (status is not null &&
                        status.StatusId == trigger.Id &&
                        (status.SourceID == player.ObjectId || !onlyMine))
                    {
                        return new DataSource()
                        {
                            Active = true,
                            TriggerId = trigger.Id,
                            Value = Math.Abs(status.RemainingTime),
                            Stacks = status.StackCount,
                            MaxStacks = trigger.MaxStacks,
                            Icon = trigger.Icon
                        };
                    }
                }
            }

            return new DataSource();
        }

        public static DataSource GetCooldownData(IEnumerable<TriggerData> triggerData, bool usable, bool inRange, bool preview)
        {
            if (preview)
            {
                return new DataSource()
                {
                    Active = true,
                    Value = 10,
                    Stacks = 2,
                    MaxStacks = 2,
                    Icon = triggerData.FirstOrDefault().Icon
                };
            }

            if (!triggerData.Any())
            {
                return new DataSource();
            }

            SpellHelpers helper = Singletons.Get<SpellHelpers>();
            TriggerData actionTrigger = triggerData.First();
            var (recastTime, recastTimeElapsed, maxCharges) = helper.GetAdjustedRecastInfo(actionTrigger.Id);

            int stacks = recastTime == 0f
                ? maxCharges
                : (int)(maxCharges * (recastTimeElapsed / recastTime));

            float chargeTime = maxCharges != 0
                ? recastTime / maxCharges
                : recastTime;

            float cooldown = chargeTime != 0 
                ? Math.Abs(recastTime - recastTimeElapsed) % chargeTime
                : 0;

            return new DataSource()
            {
                Active = usable && helper.CanUseAction(actionTrigger.Id),
                InRange = inRange && helper.GetActionInRange(actionTrigger.Id, Singletons.Get<ClientState>().LocalPlayer, Utils.FindTarget()),
                TriggerId = actionTrigger.Id,
                Value = cooldown,
                Stacks = stacks,
                MaxStacks = maxCharges,
                Icon = actionTrigger.Icon
            };
        }

        public static List<TriggerData> FindStatusEntries(string input)
        {
            ExcelSheet<LuminaStatus>? sheet = Singletons.Get<DataManager>().GetExcelSheet<LuminaStatus>();

            if (!string.IsNullOrEmpty(input) && sheet is not null)
            {
                List<TriggerData> statusList = new List<TriggerData>();

                // Add by id
                if (uint.TryParse(input, out uint value))
                {
                    if (value > 0)
                    {
                        LuminaStatus? status = sheet.GetRow(value);
                        if (status is not null)
                        {
                            statusList.Add(new TriggerData(status.Name, status.RowId, status.Icon, status.MaxStacks));
                        }
                    }
                }

                // Add by name
                if (statusList.Count == 0)
                {
                    statusList.AddRange(
                        sheet.Where(status => input.ToLower().Equals(status.Name.ToString().ToLower()))
                            .Select(status => new TriggerData(status.Name, status.RowId, status.Icon, status.MaxStacks)));
                }

                return statusList;
            }

            return new List<TriggerData>();
        }

        public static List<TriggerData> FindActionEntries(string input)
        {
            List<TriggerData> actionList = new List<TriggerData>();

            if (!string.IsNullOrEmpty(input))
            {
                actionList.AddRange(FindEntriesFromActionSheet(input));

                if (!actionList.Any())
                {
                    actionList.AddRange(FindEntriesFromActionIndirectionSheet(input));
                }

                if (!actionList.Any())
                {
                    actionList.AddRange(FindEntriesFromGeneralActionSheet(input));
                }
            }

            return actionList;
        }

        public static List<TriggerData> FindEntriesFromActionSheet(string input)
        {
            List<TriggerData> actionList = new List<TriggerData>();
            ExcelSheet<LuminaAction>? actionSheet = Singletons.Get<DataManager>().GetExcelSheet<LuminaAction>();

            if (actionSheet is null)
            {
                return actionList;
            }
            
            // Add by id
            if (uint.TryParse(input, out uint value))
            {
                if (value > 0)
                {
                    LuminaAction? action = actionSheet.GetRow(value);
                    if (action is not null && (action.IsPlayerAction || action.IsRoleAction))
                    {
                        actionList.Add(new TriggerData(action.Name, action.RowId, action.Icon, action.MaxCharges));
                    }
                }
            }

            // Add by name
            if (!actionList.Any())
            {
                foreach(LuminaAction action in actionSheet)
                {
                    if (input.ToLower().Equals(action.Name.ToString().ToLower()) && (action.IsPlayerAction || action.IsRoleAction))
                    {
                        actionList.Add(new TriggerData(action.Name, action.RowId, action.Icon, action.MaxCharges));
                    }
                }
            }
            
            return actionList;
        }

        public static List<TriggerData> FindEntriesFromActionIndirectionSheet(string input)
        {
            List<TriggerData> actionList = new List<TriggerData>();
            ExcelSheet<ActionIndirection>? actionIndirectionSheet = Singletons.Get<DataManager>().GetExcelSheet<ActionIndirection>();

            if (actionIndirectionSheet is null)
            {
                return actionList;
            }

            // Add by id
            if (uint.TryParse(input, out uint value))
            {
                foreach (ActionIndirection iAction in actionIndirectionSheet)
                {
                    LuminaAction? action = iAction.Name.Value;
                    if (action is not null && action.RowId == value)
                    {
                        actionList.Add(new TriggerData(action.Name, action.RowId, action.Icon, action.MaxCharges));
                        break;
                    }
                }
            }
            
            // Add by name
            if (!actionList.Any())
            {
                foreach (ActionIndirection indirectAction in actionIndirectionSheet)
                {
                    LuminaAction? action = indirectAction.Name.Value;
                    if (action is not null && input.ToLower().Equals(action.Name.ToString().ToLower()))
                    {
                        actionList.Add(new TriggerData(action.Name, action.RowId, action.Icon, action.MaxCharges));
                    }
                }
            }

            return actionList;
        }

        public static List<TriggerData> FindEntriesFromGeneralActionSheet(string input)
        {
            List<TriggerData> actionList = new List<TriggerData>();
            ExcelSheet<GeneralAction>? generalSheet = Singletons.Get<DataManager>().GetExcelSheet<GeneralAction>();

            if (generalSheet is null)
            {
                return actionList;
            }

            // Add by name (Add by id doesn't really work, these sheets are a mess)
            if (!actionList.Any())
            {
                foreach (GeneralAction generalAction in generalSheet)
                {
                    LuminaAction? action = generalAction.Action.Value;
                    if (action is not null && input.ToLower().Equals(generalAction.Name.ToString().ToLower()))
                    {
                        actionList.Add(new TriggerData(generalAction.Name, action.RowId, (ushort)generalAction.Icon, action.MaxCharges));
                    }
                }
            }

            return actionList;
        }
    }

    public class DataSource
    {
        public uint TriggerId;
        public bool Active;
        public bool InRange;
        public float Value;
        public int Stacks;
        public int MaxStacks;
        public ushort Icon;

        public float GetDataForSourceType(TriggerDataSource source)
        {
            return source switch
            {
                TriggerDataSource.Value => this.Value,
                TriggerDataSource.Stacks => this.Stacks,
                TriggerDataSource.MaxStacks => this.MaxStacks,
                _ => 0
            };
        }
    }

    public struct TriggerData
    {
        public string Name;
        public uint Id;
        public ushort Icon;
        public byte MaxStacks;

        public TriggerData(string name, uint id, ushort icon, byte maxStacks = 0)
        {
            Name = name;
            Id = id;
            Icon = icon;
            MaxStacks = maxStacks;
        }
    }
}
