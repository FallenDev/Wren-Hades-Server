﻿#region

using Darkages.Common;
using Darkages.Network.ClientFormats;
using Darkages.Network.Login;
using Darkages.Network.ServerFormats;
using Darkages.Scripting;
using Darkages.Storage;
using Darkages.Systems.CLI;
using Darkages.Templates;
using Darkages.Types;
using MenuInterpreter;
using MenuInterpreter.Parser;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

#endregion

namespace Darkages.Network.Game
{
    [SuppressMessage("ReSharper", "StringLiteralTypo")]
    public partial class GameServer : NetworkServer<GameClient>
    {
        public static ScriptOptions ScriptOptions;

        static GameServer()
        {
            ScriptOptions = ScriptOptions.Default
                .AddReferences(typeof(Quest).Assembly)
                .AddImports("System")
                .AddImports("System.Collections.Generic")
                .AddImports("System.IO")
                .AddImports("System.Linq")
                .AddImports("System.Text")
                .AddImports("System.Globalization")
                .AddImports("Newtonsoft.Json")
                .AddImports("Darkages.Common")
                .AddImports("Darkages.Storage")
                .AddImports("Darkages.Types")
                .AddImports("Darkages.Types.Quest");
        }

        public static void Assail(GameClient lpClient)
        {
            if (lpClient == null) throw new ArgumentNullException(nameof(lpClient));

            #region Sanity Checks

            if (lpClient.Aisling == null)
                return;

            if (lpClient.Aisling.IsDead())
                return;

            #endregion

            if (lpClient.Aisling.IsSleeping || lpClient.Aisling.IsFrozen)
            {
                lpClient.Interupt();
                return;
            }

            lpClient.MenuInterpter = null;

            if (ServerContext.Config.AssailsCancelSpells)
                CancelIfCasting(lpClient);

            var ready = DateTime.UtcNow > lpClient.LastScriptExecuted;

            var itemScripts = lpClient.Aisling.EquipmentManager.Weapon?.Item?.WeaponScripts;

            if (itemScripts != null)
                foreach (var itemScript in itemScripts.Values.Where(itemScript => itemScript != null && ready))
                    itemScript.OnUse(lpClient.Aisling,
                        targets =>
                        {
                            lpClient.LastScriptExecuted =
                                DateTime.UtcNow.AddMilliseconds(ServerContext.Config
                                    .GlobalBaseSkillDelay);
                        });

            if ((lpClient.LastAssail - DateTime.UtcNow).TotalMilliseconds >
                ServerContext.Config.GlobalBaseSkillDelay) return;

            var lastTemplate = string.Empty;
            foreach (var skill in lpClient.Aisling.GetAssails())
            {
                if (skill == null)
                    continue;

                if (!skill.CanUse())
                    continue;

                if (skill.Template == null)
                    continue;

                if (skill.Scripts == null)
                    continue;

                if (skill.InUse)
                    continue;

                if (lastTemplate == skill.Template.Name)
                    continue;

                ExecuteAbility(lpClient, skill);
                lastTemplate = skill.Template.Name;
            }

            lpClient.LastAssail = DateTime.UtcNow;
        }

        public static void CancelIfCasting(GameClient client)
        {
            if (!client.Aisling.LoggedIn) return;

            client.CastStack.Clear();
            client.Aisling.IsCastingSpell = false;
            client.Send(new ServerFormat48());
        }

        public static void ExecuteAbility(GameClient lpClient, Skill lpSkill, bool optExecuteScript = true)
        {
            lpSkill.InUse = true;

            if (optExecuteScript)
                foreach (var script in lpSkill.Scripts.Values)
                    script?.OnUse(lpClient.Aisling);

            lpSkill.NextAvailableUse =
                lpSkill.Template.Cooldown > 0
                    ? DateTime.UtcNow.AddSeconds(lpSkill.Template.Cooldown)
                    : DateTime.UtcNow.AddMilliseconds(ServerContext.Config.GlobalBaseSkillDelay);

            lpSkill.InUse = false;
        }

        public void CreateInterpreterFromMenuFile(GameClient lpClient, string lpName, Sprite obj = null)
        {
            var parser = new YamlMenuParser();
            var yamlPath = ServerContext.StoragePath +
                           string.Format(CultureInfo.CurrentCulture, @"\interactive\Menus\{0}.yaml", lpName);

            if (!File.Exists(yamlPath))
                return;

            var globals = new ScriptGlobals
            {
                actor = obj,
                client = lpClient,
                user = lpClient.Aisling
            };


            lpClient.MenuInterpter = parser.CreateInterpreterFromFile(yamlPath);
            lpClient.MenuInterpter.Actor = obj;
            lpClient.MenuInterpter.Client = lpClient;
            lpClient.MenuInterpter.OnMovedToNextStep += MenuInterpter_OnMovedToNextStep;

            lpClient.MenuInterpter.RegisterCheckpointHandler("Call", async (client, res) =>
            {
                try
                {
                    await CSharpScript.EvaluateAsync<bool>(res.Value, ScriptOptions, globals);
                    res.Result = globals.result;
                }
                catch (Exception ex)
                {
                    ServerContext.Logger(ex.Message, Microsoft.Extensions.Logging.LogLevel.Error);
                    ServerContext.Logger(ex.StackTrace, Microsoft.Extensions.Logging.LogLevel.Error);

                    res.Result = false;
                }
            });

            lpClient.MenuInterpter.RegisterCheckpointHandler("QuestCompleted", (client, res) =>
            {
                if (client.Aisling.HasQuest(res.Value))
                    res.Result = client.Aisling.HasCompletedQuest(res.Value);
            });

            lpClient.MenuInterpter.RegisterCheckpointHandler("CompleteQuest", (client, res) =>
            {
                if (!client.Aisling.HasQuest(res.Value))
                    return;

                var q = client.Aisling.GetQuest(res.Value);
                if (q == null) return;

                if (!q.Completed)
                    q.HandleQuest(client, null,
                        completed => { res.Result = completed; });
            });
        }

        public void ExitGame(GameClient client)
        {
            var redirect = new Redirect
            {
                Serial = Convert.ToString(client.Serial, CultureInfo.CurrentCulture),
                Salt = Encoding.UTF8.GetString(client.Encryption.Parameters.Salt),
                Seed = Convert.ToString(client.Encryption.Parameters.Seed, CultureInfo.CurrentCulture),
                Name = client.Aisling.Username,
                Type = "2"
            };

            if ((DateTime.UtcNow - client.LastSave).TotalSeconds > 2)
                client.Save();

            if (ServerContext.Redirects.Contains(client.Aisling.Username.ToLower()))
                ServerContext.Redirects.Remove(client.Aisling.Username.ToLower());

            client.FlushAndSend(new ServerFormat03
            {
                EndPoint = new IPEndPoint(Address, 2610),
                Redirect = redirect
            });

            client.FlushAndSend(new ServerFormat02(0x00, "\0"));
        }

        public void UpdateSettings(GameClient client)
        {
            var msg = "\t";

            foreach (var setting in client.Aisling.GameSettings.Where(setting => setting != null))
            {
                msg += setting.Enabled ? setting.EnabledSettingStr : setting.DisabledSettingStr;
                msg += "\t";
            }

            client.SendMessage(0x07, msg);
        }

        protected override void Format00Handler(LoginClient client, ClientFormat00 format)
        {
        }

        protected override void Format05Handler(GameClient client, ClientFormat05 format)
        {
            if (client?.Aisling?.Map == null)
                return;

            if (!client.MapUpdating && client.Aisling.CurrentMapId != ServerContext.Config.TransitionZone)
                return;

            SendMapData(client);
            client.MapUpdating = false;
        }

        protected override void Format06Handler(GameClient client, ClientFormat06 format)
        {
            #region Sanity Checks

            if (client?.Aisling == null)
                return;

            if (!client.Aisling.LoggedIn)
                return;

            if (client.Aisling.Map == null)
                return;

            if (!client.Aisling.Map.Ready)
                return;

            #endregion

            if (client.Aisling.IsSleeping || client.Aisling.IsFrozen || client.Aisling.IsParalyzed)
            {
                client.SendLocation();
                client.UpdateDisplay();
                return;
            }

            client.Aisling.CanReact = true;

            if (client.Aisling.Skulled)
            {
                if (!ServerContext.Config.CanMoveDuringReap)
                    client.SendLocation();

                client.SystemMessage(ServerContext.Config.ReapMessageDuringAction);
                return;
            }

            if (client.IsRefreshing && ServerContext.Config.CancelWalkingIfRefreshing)
                return;

            if (client.Aisling.IsCastingSpell && ServerContext.Config.CancelCastingWhenWalking) CancelIfCasting(client);

            client.Aisling.Direction = format.Direction;

            if (ServerContext.Config.LimitWalkingSpeed)
            {
                if (client.IsSpeedHacking)
                {
                    client.Refresh(true);
                    return;
                }
            }

            var success = client.Aisling.Walk();


            if (success)
            {
                client.LastMovement = DateTime.UtcNow;

                if (client.Aisling.AreaId == ServerContext.Config.TransitionZone)
                {
                    client.Aisling.PortalSession = new PortalSession { IsMapOpen = false };
                    client.Aisling.PortalSession.TransitionToMap(client);
                    return;
                }

                CheckWalkOverPopups(client);
                CheckWarpTransitions(client);


                if (client.Aisling.Map == null || !client.Aisling.Map.Scripts.Any())
                    return;

                foreach (var script in client.Aisling.Map.Scripts.Values)
                {
                    script.OnPlayerWalk(client, client.Aisling.LastPosition, client.Aisling.Position);
                }
            }
            else
            {
                client.Refresh();
            }
        }

        protected override void Format07Handler(GameClient client, ClientFormat07 format)
        {
            #region Sanity Checks

            if (client?.Aisling == null)
                return;

            if (!client.Aisling.LoggedIn)
                return;

            if (client.Aisling.IsDead())
                return;

            #endregion

            var objs = GetObjects(client.Aisling.Map,
                i => i.XPos == format.Position.X && i.YPos == format.Position.Y,
                Get.Items | Get.Money);

            if (objs == null)
                return;

            foreach (var obj in objs.Reverse())
            {
                if (obj?.CurrentMapId != client.Aisling.CurrentMapId)
                    continue;

                if (!(client.Aisling.Position.DistanceFrom(obj.Position) <=
                      ServerContext.Config.ClickLootDistance))
                    continue;

                if (obj is Money money)
                {
                    money.GiveTo(money.Amount, client.Aisling);
                }
                else if (obj is Item item)
                {
                    if ((item.Template.Flags & ItemFlags.Trap) == ItemFlags.Trap) continue;

                    if (item.Cursed)
                    {
                        Sprite first = null;

                        if (item.AuthenticatedAislings != null)
                        {
                            foreach (var i in item.AuthenticatedAislings)
                            {
                                if (i.Serial != client.Aisling.Serial)
                                    continue;

                                first = i;
                                break;
                            }

                            if (item.AuthenticatedAislings != null && first == null)
                            {
                                client.SendMessage(0x02, ServerContext.Config.CursedItemMessage);
                                break;
                            }
                        }

                        if (item.GiveTo(client.Aisling))
                        {
                            item.Remove();
                            break;
                        }

                        item.XPos = client.Aisling.XPos;
                        item.YPos = client.Aisling.YPos;
                        item.Show(Scope.NearbyAislings, new ServerFormat07(new[] {obj}));
                        break;
                    }

                    if (item.GiveTo(client.Aisling))
                    {
                        item.Remove();

                        var popupTemplate = ServerContext.GlobalPopupCache
                            .OfType<ItemPickupPopup>().FirstOrDefault(i => i.ItemName == (obj as Item)?.Template.Name);

                        if (popupTemplate != null && client.Aisling.ActiveReactors.ContainsKey(popupTemplate.YamlKey))
                            if (item.Position.X == client.Aisling.X && item.Position.Y == client.Aisling.Y
                                                                    && item.Owner == client.Aisling.Serial)
                            {
                                popupTemplate.SpriteId = item.Template.DisplayImage;

                                var popup = Popup.Create(client, popupTemplate);

                                if (popup != null)
                                    if (client.MenuInterpter == null)
                                    {
                                        CreateInterpreterFromMenuFile(client, popup.Template.YamlKey);

                                        if (client.MenuInterpter != null)
                                        {
                                            client.MenuInterpter.Start();
                                            client.ShowCurrentMenu(popup, null, client.MenuInterpter.GetCurrentStep());
                                        }
                                    }
                            }

                        if (item.Scripts != null)
                            foreach (var itemScript in item.Scripts?.Values)
                                itemScript?.OnPickedUp(client.Aisling, format.Position, client.Aisling.Map);

                        break;
                    }

                    item.XPos = client.Aisling.XPos;
                    item.YPos = client.Aisling.YPos;
                    item.Show(Scope.NearbyAislings, new ServerFormat07(new[] {obj}));

                    break;
                }
            }
        }

        protected override void Format08Handler(GameClient client, ClientFormat08 format)
        {
            #region Sanity Checks (alot can go wrong if you remove this)

            if (client?.Aisling == null)
                return;

            if (!client.Aisling.LoggedIn)
                return;

            if (client.Aisling.Map == null || !client.Aisling.Map.Ready)
                return;

            #endregion

            var item = client.Aisling.Inventory.Get(i => i != null && i.Slot == format.ItemSlot).FirstOrDefault();

            if (item?.Template == null)
                return;

            if (!item.Template.Flags.HasFlag(ItemFlags.Dropable))
            {
                client.SendMessage(Scope.Self, 0x02, ServerContext.Config.CantDropItemMsg);
                return;
            }

            var popupTemplate = ServerContext.GlobalPopupCache
                .OfType<ItemDropPopup>().FirstOrDefault(i => i.ItemName == item.Template.Name);

            if (popupTemplate != null && client.Aisling.ActiveReactors.ContainsKey(popupTemplate.YamlKey))
            {
                popupTemplate.SpriteId = item.Template.DisplayImage;

                var popup = Popup.Create(client, popupTemplate);

                if (popup != null)
                    if (client.MenuInterpter == null)
                    {
                        CreateInterpreterFromMenuFile(client, popup.Template.YamlKey);

                        if (client.MenuInterpter != null)
                        {
                            client.MenuInterpter.Start();
                            client.ShowCurrentMenu(popup, null, client.MenuInterpter.GetCurrentStep());
                        }
                    }
            }

            var itemPosition = new Position(format.X, format.Y);
            Item copy;


            if (client.Aisling.Position.DistanceFrom(itemPosition.X, itemPosition.Y) > 2)
            {
                client.SendMessage(Scope.Self, 0x02, ServerContext.Config.CantDoThat);
                return;
            }

            if (client.Aisling.Map.IsWall(format.X, format.Y))
                if (client.Aisling.XPos != format.X || client.Aisling.YPos != format.Y)
                {
                    client.SendMessage(Scope.Self, 0x02, ServerContext.Config.CantDoThat);
                    return;
                }

            if ((item.Template.Flags & ItemFlags.Stackable) == ItemFlags.Stackable)
            {
                var remaining = item.Stacks - format.ItemAmount;

                if (remaining <= 0)
                {
                    copy = Clone<Item>(item);

                    if (client.Aisling.EquipmentManager.RemoveFromInventory(item, true))
                    {
                        copy.Release(client.Aisling, new Position(format.X, format.Y));
                        client.SendStats(StatusFlags.StructA);
                    }
                }
                else
                {
                    var nitem = Clone<Item>(item);
                    nitem.Stacks = (byte)format.ItemAmount;
                    nitem.Release(client.Aisling, new Position(format.X, format.Y));

                    item.Stacks = (byte)remaining;
                    client.Aisling.Inventory.Set(item, false);

                    client.Send(new ServerFormat10(item.Slot));
                    client.Send(new ServerFormat0F(item));
                }
            }
            else
            {
                copy = Clone<Item>(item);

                if (client.Aisling.EquipmentManager.RemoveFromInventory(item, true))
                    copy.Release(client.Aisling, new Position(format.X, format.Y));

                //Invoke area Scripts if found for the current map. and trigger OnItemDropped.
                if (client.Aisling.Map != null && client.Aisling.Map.Scripts.Any())
                {
                    foreach (var script in client.Aisling.Map.Scripts.Values)
                    {
                        script.OnItemDropped(client, copy, itemPosition);
                    }
                }
            }

            copy = Clone<Item>(item);

            if (copy?.Scripts == null) return;
            foreach (var itemScript in (copy.Scripts?.Values).Where(itemScript => client.Aisling?.Map != null))
                itemScript?.OnDropped(client.Aisling, new Position(format.X, format.Y), client.Aisling.Map);

            //Invoke area Scripts if found for the current map. and trigger OnItemDropped.
            if (client.Aisling.Map != null && client.Aisling.Map.Scripts.Any())
            {
                foreach (var script in client.Aisling.Map.Scripts.Values)
                {
                    script.OnItemDropped(client, copy, itemPosition);
                }
            }
        }

        protected override void Format0BHandler(GameClient client, ClientFormat0B format)
        {
            LeaveGame(client, format);
        }

        protected override void Format0EHandler(GameClient client, ClientFormat0E format)
        {
            bool ParseCommand()
            {
                if (client.Aisling.GameMaster || client.Aisling.Developer)
                {
                    if (format.Text.StartsWith("/"))
                    {
                        Commander.ParseChatMessage(client, format.Text);
                        return true;
                    }
                }

                return false;
            }

            #region Sanity Checks

            if (client?.Aisling == null)
                return;

            if (!client.Aisling.LoggedIn)
                return;

            #endregion

            var response = new ServerFormat0D
            {
                Serial = client.Aisling.Serial,
                Type = format.Type,
                Text = string.Empty
            };

            IEnumerable<Aisling> audience;

            if (ParseCommand())
                return;

            switch (format.Type)
            {
                case 0x00:
                    response.Text = $"{client.Aisling.Username}: {format.Text}";
                    audience = client.GetObjects<Aisling>(client.Aisling.Map,
                        n => client.Aisling.WithinRangeOf(n));
                    break;

                case 0x01:
                    response.Text = $"{client.Aisling.Username}! {format.Text}";
                    audience = client.GetObjects<Aisling>(client.Aisling.Map,
                        n => client.Aisling.CurrentMapId == n.CurrentMapId);
                    break;

                case 0x02:
                    response.Text = format.Text;
                    audience = client.GetObjects<Aisling>(client.Aisling.Map,
                        n => client.Aisling.WithinRangeOf(n, false));
                    break;

                default:
                    ClientDisconnected(client);
                    return;
            }

            var nearbyMundanes = client.Aisling.MundanesNearby();

            foreach (var npc in nearbyMundanes)
            {
                if (npc?.Scripts == null)
                    continue;

                foreach (var script in npc.Scripts?.Values)
                    script?.OnGossip(this, client, format.Text);
            }

            client.Aisling.Show(Scope.DefinedAislings, response, audience.ToArray());
        }

        protected override void Format0FHandler(GameClient client, ClientFormat0F format)
        {
            #region Sanity Checks

            if (client?.Aisling == null)
                return;

            if (!client.Aisling.LoggedIn)
                return;

            if (client.Aisling.IsDead())
                return;

            #endregion

            if (client.Aisling.Skulled)
            {
                client.SystemMessage(ServerContext.Config.ReapMessageDuringAction);
                return;
            }


            var spellReq = client.Aisling.SpellBook.Get(i => i != null && i.Slot == format.Index).FirstOrDefault();

            if (spellReq == null)
                return;

            //abort cast?
            if (client.Aisling.IsSleeping || client.Aisling.IsFrozen)
                if (spellReq.Template.Name != "ao suain" && spellReq.Template.Name != "ao pramh")
                {
                    CancelIfCasting(client);
                    return;
                }


            var info = new CastInfo
            {
                Slot = format.Index,
                Target = format.Serial,
                Position = format.Point,
                Data = format.Data,
            };

            if (info.Position == null) info.Position = new Position(client.Aisling.X, client.Aisling.Y);

            lock (client.CastStack)
            {
                client.CastStack?.Push(info);
            }
        }

        protected override void Format10Handler(GameClient client, ClientFormat10 format)
        {
            lock (ServerContext.SyncLock)
            {
                void ValidateClient()
                {
                    if (!ServerContext.Redirects.Contains(client.Aisling.Username.ToLower()))
                    {
                        //disconnect, unverified login.
                        ClientDisconnected(client);
                    }
                    else
                    {
                        ServerContext.Redirects.Remove(client.Aisling.Username.ToLower());
                    }
                }

                #region Sanity Checks

                if (client == null)
                    return;

                #endregion

                EnterGame(client, format);

                ValidateClient();
            }
        }

        protected override void Format11Handler(GameClient client, ClientFormat11 format)
        {
            client.Aisling.Direction = format.Direction;

            if (client.Aisling.Skulled) client.SystemMessage(ServerContext.Config.ReapMessageDuringAction);

            client.Aisling.Show(Scope.NearbyAislings, new ServerFormat11
            {
                Direction = client.Aisling.Direction,
                Serial = client.Aisling.Serial
            });
        }

        protected override void Format13Handler(GameClient client, ClientFormat13 format)
        {
            #region Sanity Checks

            if (client?.Aisling == null)
                return;

            if (!client.Aisling.LoggedIn)
                return;

            if (client.Aisling.IsDead())
                return;

            #endregion

            if (client.Aisling.Skulled)
            {
                client.SystemMessage(ServerContext.Config.ReapMessageDuringAction);
                return;
            }

            if (client.Aisling.IsSleeping || client.Aisling.IsFrozen)
            {
                client.Interupt();
                return;
            }

            Assail(client);
        }

        protected override void Format18Handler(GameClient client, ClientFormat18 format)
        {
            #region Sanity Checks

            if (client?.Aisling == null)
                return;

            if (!client.Aisling.LoggedIn)
                return;

            if (client.IsRefreshing)
                return;

            #endregion

            client.Aisling.Show(Scope.Self, new ServerFormat36(client));
        }

        protected override void Format19Handler(GameClient client, ClientFormat19 format)
        {
            if (client?.Aisling == null)
                return;

            if (format == null)
                return;

            if (DateTime.UtcNow.Subtract(client.LastWhisperMessageSent).TotalSeconds < 0.30)
                return;

            if (format.Name.Length > 24)
                return;

            client.LastWhisperMessageSent = DateTime.UtcNow;

            if (format.Name == "!!" && !string.IsNullOrEmpty(client.Aisling.Clan))
            {
                client.Aisling.Show(Scope.Clan, new ServerFormat0A(0x02, "{=o" + $"{client.Aisling.Username}> " + "{=a" + format.Message));
            }
            else if (format.Name == "!!" && string.IsNullOrEmpty(client.Aisling.Clan))
            {
                client.SystemMessage("You are not in a guild.");
                return;
            }

            var user = Clients.FirstOrDefault(i => i?.Aisling != null && i.Aisling.LoggedIn && i.Aisling.Username.ToLower() ==
                                                   format.Name.ToLower(CultureInfo.CurrentCulture));

            if (user == null)
                client.SendMessage(0x02, string.Format(CultureInfo.CurrentCulture, "{0} is nowhere to be found.", format.Name));

            if (user == null)
                return;

            user.SendMessage(0x00, string.Format(CultureInfo.CurrentCulture, "{0}\" {1}", client.Aisling.Username, format.Message));
            client.SendMessage(0x00, string.Format(CultureInfo.CurrentCulture, "{0}> {1}", user.Aisling.Username, format.Message));
        }

        protected override void Format1BHandler(GameClient client, ClientFormat1B format)
        {
            if (client.Aisling.GameSettings == null)
                return;

            var settingKeys = client.Aisling.GameSettings.ToArray();

            if (settingKeys.Length == 0)
                return;

            var settingIdx = format.Index;

            if (settingIdx > 0)
            {
                settingIdx--;

                if (settingIdx < 0)
                    return;

                var setting = settingKeys[settingIdx];
                setting.Toggle();

                UpdateSettings(client);
            }
            else
            {
                UpdateSettings(client);
            }
        }

        protected override void Format1CHandler(GameClient client, ClientFormat1C format)
        {
            #region Sanity Checks (alot can go wrong if you remove this)

            if (client?.Aisling?.Map == null || !client.Aisling.Map.Ready)
                return;

            if (!client.Aisling.LoggedIn)
                return;

            if (client.Aisling.Dead)
                return;

            #endregion

            if (client.Aisling.Skulled)
            {
                client.SystemMessage(ServerContext.Config.ReapMessageDuringAction);
                return;
            }

            var slot = format.Index;
            var item = client.Aisling.Inventory.Get(i => i != null && i.Slot == slot).FirstOrDefault();

            if (item == null)
                return;

            var activated = false;

            if (item.Template == null)
                return;

            client.LastActivatedSlot = slot;

            if (!string.IsNullOrEmpty(item.Template.ScriptName))
                if (item.Scripts == null)
                    item.Scripts = ScriptManager.Load<ItemScript>(item.Template.ScriptName, item);

            if (!string.IsNullOrEmpty(item.Template.WeaponScript))
                if (item.WeaponScripts == null)
                    item.WeaponScripts = ScriptManager.Load<WeaponScript>(item.Template.WeaponScript, item);

            if (item.Scripts == null)
            {
                client.SendMessage(0x02, ServerContext.Config.CantUseThat);
            }
            else
            {
                foreach (var script in item.Scripts.Values) script?.OnUse(client.Aisling, slot);

                activated = true;
            }

            if (!activated)
                return;

            if (!item.Template.Flags.HasFlag(ItemFlags.Stackable))
                return;

            if (!item.Template.Flags.HasFlag(ItemFlags.Consumable))
                return;

            var stack = item.Stacks - 1;

            if (stack > 0)
            {
                item.Stacks -= 1;

                client.Aisling.Inventory.Set(item, false);

                client.Send(new ServerFormat10(item.Slot));
                client.Send(new ServerFormat0F(item));
            }
            else
            {
                client.Aisling.Inventory.Remove(item.Slot);
                client.Send(new ServerFormat10(item.Slot));
            }
        }

        protected override void Format1DHandler(GameClient client, ClientFormat1D format)
        {
            #region Sanity Checks

            if (client?.Aisling == null)
                return;

            if (!client.Aisling.LoggedIn)
                return;

            if (client.IsRefreshing)
                return;

            if (client.Aisling.IsDead())
                return;

            #endregion

            if (client.Aisling.Skulled)
            {
                client.SystemMessage(ServerContext.Config.ReapMessageDuringAction);
                return;
            }

            var id = format.Number;

            if (id > 35)
                return;

            client.Aisling.Show(Scope.NearbyAislings,
                new ServerFormat1A(client.Aisling.Serial, (byte) (id + 9), 64));
        }

        protected override void Format24Handler(GameClient client, ClientFormat24 format)
        {
            if (client?.Aisling == null)
                return;

            if (client.Aisling.IsSleeping || client.Aisling.IsFrozen)
                return;

            if (client.Aisling.Skulled)
            {
                client.SystemMessage(ServerContext.Config.ReapMessageDuringAction);
                return;
            }

            if (client.Aisling.GoldPoints >= format.GoldAmount)
            {
                client.Aisling.GoldPoints -= format.GoldAmount;
                if (client.Aisling.GoldPoints <= 0)
                    client.Aisling.GoldPoints = 0;

                client.SendMessage(Scope.Self, 0x02, ServerContext.Config.YouDroppedGoldMsg);
                client.SendMessage(Scope.NearbyAislingsExludingSelf, 0x02,
                    ServerContext.Config.UserDroppedGoldMsg.Replace("noname", client.Aisling.Username));

                Money.Create(client.Aisling, format.GoldAmount, new Position(format.X, format.Y));
                client.SendStats(StatusFlags.StructC);
            }
            else
            {
                client.SendMessage(0x02, ServerContext.Config.NotEnoughGoldToDropMsg);
            }
        }

        protected override void Format29Handler(GameClient client, ClientFormat29 format)
        {
            client.Send(new ServerFormat4B(format.ID, 0));
            client.Send(new ServerFormat4B(format.ID, 1, format.ItemSlot));

            var obj = GetObject<Mundane>(client.Aisling.Map, i => i.Serial.Equals((int)format.ID));

            if (obj?.Scripts == null) return;
            foreach (var (_, script) in obj.Scripts.Select(x => (x.Key, x.Value)))
            {
                script.OnDropped(client, format.ItemSlot);
            }
        }

        protected override void Format2AHandler(GameClient client, ClientFormat2A format)
        {
        }

        protected override void Format2DHandler(GameClient client, ClientFormat2D format)
        {
            #region Sanity Checks

            if (client?.Aisling == null)
                return;

            if (!client.Aisling.LoggedIn)
                return;

            #endregion

            if (client.Aisling.Skulled)
            {
                client.SystemMessage(ServerContext.Config.ReapMessageDuringAction);
                return;
            }

            client.Send(new ServerFormat39(client.Aisling));
            client.Aisling.ProfileOpen = true;
        }

        protected override void Format2EHandler(GameClient client, ClientFormat2E format)
        {
            #region Sanity Checks

            if (client?.Aisling == null)
                return;

            if (!client.Aisling.LoggedIn)
                return;

            if (client.IsRefreshing)
                return;

            if (client.Aisling.IsDead())
                return;

            #endregion

            if (format.Type != 0x02)
                return;

            var player = GetObject<Aisling>(client.Aisling.Map,
                i => i.Username.ToLower() == format.Name.ToLower() &&
                     i.WithinRangeOf(client.Aisling));

            if (player == null)
            {
                client.SendMessage(0x02, ServerContext.Config.BadRequestMessage);
                return;
            }

            if (player.PartyStatus != GroupStatus.AcceptingRequests)
            {
                client.SendMessage(0x02,
                    ServerContext.Config.GroupRequestDeclinedMsg.Replace("noname", player.Username));
                return;
            }

            if (Party.AddPartyMember(client.Aisling, player))
                client.Aisling.PartyStatus = GroupStatus.AcceptingRequests;
        }

        protected override void Format2FHandler(GameClient client, ClientFormat2F format)
        {
            var mode = client.Aisling.PartyStatus;

            if (mode == GroupStatus.AcceptingRequests)
                mode = GroupStatus.NotAcceptingRequests;
            else if (mode == GroupStatus.NotAcceptingRequests)
                mode = GroupStatus.AcceptingRequests;

            client.Aisling.PartyStatus = mode;

            if (client.Aisling.PartyStatus == GroupStatus.NotAcceptingRequests)
            {
                if (client.Aisling.LeaderPrivileges)
                {
                    if (!ServerContext.GlobalGroupCache.ContainsKey(client.Aisling.GroupId))
                        return;

                    var group = ServerContext.GlobalGroupCache[client.Aisling.GroupId];
                    if (group != null)
                        Party.DisbandParty(group);
                }

                Party.RemovePartyMember(client.Aisling);
            }
        }

        protected override void Format30Handler(GameClient client, ClientFormat30 format)
        {
            #region Sanity Checks

            if (client?.Aisling == null)
                return;

            if (!client.Aisling.LoggedIn)
                return;

            if (client.IsRefreshing)
                return;

            CancelIfCasting(client);

            if (client.Aisling.IsDead())
                return;

            #endregion

            if (client.Aisling.IsSleeping || client.Aisling.IsFrozen || client.Aisling.IsCastingSpell)
            {
                client.Interupt();
                return;
            }

            if (client.Aisling.Skulled)
            {
                client.SystemMessage(ServerContext.Config.ReapMessageDuringAction);
                return;
            }

            switch (format.PaneType)
            {
                case Pane.Inventory:
                {
                    if (format.MovingTo - 1 > client.Aisling.Inventory.Length)
                        return;
                    if (format.MovingFrom - 1 > client.Aisling.Inventory.Length)
                        return;
                    if (format.MovingTo - 1 < 0)
                        return;
                    if (format.MovingFrom - 1 < 0)
                        return;

                    client.Send(new ServerFormat10(format.MovingFrom));
                    client.Send(new ServerFormat10(format.MovingTo));

                    var a = client.Aisling.Inventory.Remove(format.MovingFrom);
                    var b = client.Aisling.Inventory.Remove(format.MovingTo);

                    if (a != null)
                    {
                        a.Slot = format.MovingTo;
                        client.Send(new ServerFormat0F(a));
                        client.Aisling.Inventory.Set(a, false);
                    }

                    if (b != null)
                    {
                        b.Slot = format.MovingFrom;
                        client.Send(new ServerFormat0F(b));
                        client.Aisling.Inventory.Set(b, false);
                    }
                }
                    break;

                case Pane.Skills:
                {
                    if (format.MovingTo - 1 > client.Aisling.SkillBook.Length)
                        return;
                    if (format.MovingFrom - 1 > client.Aisling.SkillBook.Length)
                        return;
                    if (format.MovingTo - 1 < 0)
                        return;
                    if (format.MovingFrom - 1 < 0)
                        return;

                    client.Send(new ServerFormat2D(format.MovingFrom));
                    client.Send(new ServerFormat2D(format.MovingTo));

                    var a = client.Aisling.SkillBook.Remove(format.MovingFrom);
                    var b = client.Aisling.SkillBook.Remove(format.MovingTo);

                    if (a != null)
                    {
                        a.Slot = format.MovingTo;
                        client.Send(new ServerFormat2C(a.Slot, a.Icon, a.Name));
                        client.Aisling.SkillBook.Set(a, false);
                    }

                    if (b != null)
                    {
                        b.Slot = format.MovingFrom;
                        client.Send(new ServerFormat2C(b.Slot, b.Icon, b.Name));
                        client.Aisling.SkillBook.Set(b, false);
                    }
                }
                    break;

                case Pane.Spells:
                {
                    if (format.MovingTo - 1 > client.Aisling.SpellBook.Length)
                        return;
                    if (format.MovingFrom - 1 > client.Aisling.SpellBook.Length)
                        return;
                    if (format.MovingTo - 1 < 0)
                        return;
                    if (format.MovingFrom - 1 < 0)
                        return;

                    client.Send(new ServerFormat18(format.MovingFrom));
                    client.Send(new ServerFormat18(format.MovingTo));

                    var a = client.Aisling.SpellBook.Remove(format.MovingFrom);
                    var b = client.Aisling.SpellBook.Remove(format.MovingTo);

                    if (a != null)
                    {
                        a.Slot = format.MovingTo;
                        client.Send(new ServerFormat17(a));
                        client.Aisling.SpellBook.Set(a, false);
                    }

                    if (b != null)
                    {
                        b.Slot = format.MovingFrom;
                        client.Send(new ServerFormat17(b));
                        client.Aisling.SpellBook.Set(b, false);
                    }
                }
                    break;

                case Pane.Tools:
                {
                    if (format.MovingTo - 1 > client.Aisling.SpellBook.Length)
                        return;
                    if (format.MovingFrom - 1 > client.Aisling.SpellBook.Length)
                        return;
                    if (format.MovingTo - 1 < 0)
                        return;
                    if (format.MovingFrom - 1 < 0)
                        return;

                    client.Send(new ServerFormat18(format.MovingFrom));
                    client.Send(new ServerFormat18(format.MovingTo));

                    var a = client.Aisling.SpellBook.Remove(format.MovingFrom);
                    var b = client.Aisling.SpellBook.Remove(format.MovingTo);

                    if (a != null)
                    {
                        a.Slot = format.MovingTo;
                        client.Send(new ServerFormat17(a));
                        client.Aisling.SpellBook.Set(a, false);
                    }

                    if (b != null)
                    {
                        b.Slot = format.MovingFrom;
                        client.Send(new ServerFormat17(b));
                        client.Aisling.SpellBook.Set(b, false);
                    }
                }
                    break;

                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        protected override void Format32Handler(GameClient client, ClientFormat32 format)
        {

        }

        protected override void Format38Handler(GameClient client, ClientFormat38 format)
        {
            #region Sanity Checks

            if (client?.Aisling == null)
                return;

            if (!client.Aisling.LoggedIn)
                return;

            if (client.IsRefreshing)
                return;

            #endregion

            client.LeaveArea(true);
            client.EnterArea();

            client.LastClientRefresh = DateTime.UtcNow;
        }

        protected override void Format39Handler(GameClient client, ClientFormat39 format)
        {
            #region Sanity Checks

            if (client?.Aisling == null)
                return;

            CancelIfCasting(client);

            #endregion

            if (client.Aisling.Skulled)
            {
                client.SystemMessage(ServerContext.Config.ReapMessageDuringAction);
                return;
            }

            if (client.Aisling.IsSleeping || client.Aisling.IsFrozen)
            {
                client.Interupt();
                return;
            }

            var objId = (uint) format.Serial;

            if (objId > 0 && objId < int.MaxValue)
            {
                var obj = GetObject<Mundane>(client.Aisling.Map, i => i.Serial == objId);

                if (obj != null)
                {
                    var menu = client.MenuInterpter;
                    if (menu != null)
                    {
                        var selectedAnswer = menu.GetCurrentStep()?.Answers.ElementAt(format.Step - 1);

                        if (selectedAnswer != null) client.ShowCurrentMenu(obj, null, menu.Move(selectedAnswer.Id));
                    }
                }
                else
                {
                    var popup = Popup.GetById(objId);

                    if (popup != null)
                    {
                        var menu = client.MenuInterpter;

                        var selectedAnswer = menu?.GetCurrentStep()?.Answers.ElementAt(format.Step - 1);
                        if (selectedAnswer != null)
                        {
                            client.ShowCurrentMenu(popup, null, menu.Move(selectedAnswer.Id));
                            return;
                        }
                    }
                }
            }

            if (format.Serial != ServerContext.Config.HelperMenuId)
            {
                var objects = GetObjects(null, i => i.Serial == format.Serial, Get.All);

                foreach (var m in objects)
                {
                    var npc = m as Mundane;
                    if (npc?.Scripts?.Values == null) continue;
                    foreach (var script in npc.Scripts?.Values)
                    {
                        script.OnResponse(this, client, format.Step, format.Args);
                    }
                }
            }
            else
            {
                if (format.Serial == ServerContext.Config.HelperMenuId &&
                    ServerContext.GlobalMundaneTemplateCache.ContainsKey(ServerContext.Config
                        .HelperMenuTemplateKey))
                {
                    if (client.Aisling.IsSleeping || client.Aisling.IsFrozen)
                        return;

                    var helper = new UserHelper(this, new Mundane
                    {
                        Serial = ServerContext.Config.HelperMenuId,
                        Template = ServerContext.GlobalMundaneTemplateCache[
                            ServerContext.Config.HelperMenuTemplateKey]
                    });

                    helper.OnResponse(this, client, format.Step, format.Args);
                }
            }
        }

        protected override void Format3AHandler(GameClient client, ClientFormat3A format)
        {
            #region Sanity Checks

            if (client?.Aisling == null)
                return;

            CancelIfCasting(client);

            #endregion

            if (client.Aisling.Skulled)
            {
                client.SystemMessage(ServerContext.Config.ReapMessageDuringAction);
                return;
            }

            if (client.Aisling.IsSleeping || client.Aisling.IsFrozen)
            {
                client.Interupt();
                return;
            }

            if (format.Step == 0 && format.ScriptId == ushort.MaxValue)
            {
                client.CloseDialog();
                return;
            }

            var objId = format.Serial;

            if (objId > 0 && objId < int.MaxValue)
            {
                var obj = GetObject<Mundane>(client.Aisling.Map, i => i.Serial == objId);

                if (obj != null)
                {
                    var mundane = obj;
                    if (mundane.Scripts != null)
                        foreach (var script in mundane.Scripts?.Values)
                            script.OnResponse(this, client, format.Step, format.Input);

                    if (client.MenuInterpter == null)
                        return;

                    var interpreter = client.MenuInterpter;

                    if (format.Step > 2)
                    {
                        var back = interpreter.GetCurrentStep().Answers.FirstOrDefault(i => i.Text == "back");

                        if (back != null)
                            client.ShowCurrentMenu(obj, interpreter.GetCurrentStep(), interpreter.Move(back.Id));
//                        else
//                            client.CloseDialog();
                    }

                    if (format.Step == 1)
                    {
                        var next = interpreter.GetCurrentStep().Answers.FirstOrDefault(i => i.Text == "next");

                        if (next != null)
                        {
                            client.ShowCurrentMenu(obj, interpreter.GetCurrentStep(), interpreter.Move(next.Id));
                        }
                        else
                        {
                            var complete = interpreter.GetCurrentStep().Answers.FirstOrDefault(i => i.Text == null);

                            if (complete != null)
                            {
                                client.ShowCurrentMenu(obj, null, interpreter.Move(complete.Id));
                            }
                            else
                            {
                                var last = interpreter.GetCurrentStep().Answers
                                    .FirstOrDefault(i => i.Text == "complete");
                                if (last != null) client.ShowCurrentMenu(obj, null, interpreter.Move(last.Id));
                            }
                        }
                    }

                    if (format.Step < 1 || format.Step == 2)
                    {
                        var step = interpreter.GetCurrentStep();

                        if (step == null) return;

                        var close = step.Answers.FirstOrDefault(i => i.Text == "close");

                        if (close != null)
                            client.CloseDialog();
                    }


                    return;
                }

                var popup = Popup.GetById(objId);

                if (popup != null)
                {
                    if (client.MenuInterpter == null)
                        return;

                    var interpreter = client.MenuInterpter;

                    if (format.Step > 2)
                    {
                        var back = interpreter.GetCurrentStep().Answers.FirstOrDefault(i => i.Text == "back");

                        if (back != null)
                            client.ShowCurrentMenu(popup, interpreter.GetCurrentStep(), interpreter.Move(back.Id));
                        //else
                        //    client.CloseDialog();
                    }

                    if (format.Step == 1)
                    {
                        var next = interpreter.GetCurrentStep().Answers.FirstOrDefault(i => i.Text == "next");

                        if (next != null)
                        {
                            client.ShowCurrentMenu(popup, interpreter.GetCurrentStep(), interpreter.Move(next.Id));
                        }
                        else
                        {
                            var complete = interpreter.GetCurrentStep().Answers.FirstOrDefault(i => i.Text == null);

                            if (complete != null)
                            {
                                client.ShowCurrentMenu(popup, null, interpreter.Move(complete.Id));
                            }
                            else
                            {
                                var last = interpreter.GetCurrentStep().Answers
                                    .FirstOrDefault(i => i.Text == "complete");
                                if (last != null) client.ShowCurrentMenu(popup, null, interpreter.Move(last.Id));
                            }
                        }
                    }

                    if (format.Step < 1 || format.Step == 2)
                    {
                        var step = interpreter.GetCurrentStep();

                        if (step == null) return;

                        var close = step.Answers.FirstOrDefault(i => i.Text == "close");

                        if (close != null) client.CloseDialog();
                    }
                }
            }

            if (format.ScriptId == ushort.MaxValue)
            {
                if (client.Aisling.ActiveReactor == null || client.Aisling.ActiveReactor.Decorators == null)
                    return;

                switch (format.Step)
                {
                    case 0:
                        foreach (var script in client.Aisling.ActiveReactor.Decorators.Values)
                            script.OnClose(client.Aisling);
                        break;

                    case 255:
                        foreach (var script in client.Aisling.ActiveReactor.Decorators.Values)
                            script.OnBack(client.Aisling);
                        break;

                    case 0xFFFF:
                        foreach (var script in client.Aisling.ActiveReactor.Decorators.Values)
                            script.OnBack(client.Aisling);
                        break;

                    case 2:
                        foreach (var script in client.Aisling.ActiveReactor.Decorators.Values)
                            script.OnClose(client.Aisling);
                        break;

                    case 1:
                        foreach (var script in client.Aisling.ActiveReactor.Decorators.Values)
                            script.OnNext(client.Aisling);
                        break;
                }
            }
            else
            {
                client.DlgSession?.Callback?.Invoke(this, client, format.Step, format.Input ?? string.Empty);
            }
        }

        protected override void Format3BHandler(GameClient client, ClientFormat3B format)
        {
            if (client.Aisling.Skulled)
            {
                client.SystemMessage(ServerContext.Config.ReapMessageDuringAction);
                return;
            }


            if (format.Type == 0x01)
            {
                client.FlushAndSend(new BoardList(ServerContext.Community));
                return;
            }

            if (format.Type == 0x02)
            {
                if (format.BoardIndex == 0)
                {
                    var clone = Clone<Board>(ServerContext.Community[format.BoardIndex]);
                    {
                        clone.Client = client;
                        client.FlushAndSend(clone);
                    }
                    return;
                }

                var boards = ServerContext.GlobalBoardCache.Select(i => i.Value)
                    .SelectMany(i => i.Where(n => n.Index == format.BoardIndex))
                    .FirstOrDefault();

                if (boards != null)
                    client.FlushAndSend(boards);

                return;
            }

            if (format.Type == 0x03)
            {
                var index = format.TopicIndex - 1;

                var boards = ServerContext.GlobalBoardCache.Select(i => i.Value)
                    .SelectMany(i => i.Where(n => n.Index == format.BoardIndex))
                    .FirstOrDefault();

                if (boards != null &&
                    boards.Posts.Count > index)
                {
                    var post = boards.Posts[index];
                    client.FlushAndSend(post);
                    return;
                }

                client.FlushAndSend(new ForumCallback("Unable to retrieve more.", 0x06, true));
                return;
            }

            if (format.Type == 0x06)
            {
                var boards = ServerContext.GlobalBoardCache.Select(i => i.Value)
                    .SelectMany(i => i.Where(n => n.Index == format.BoardIndex))
                    .FirstOrDefault();

                if (boards != null)
                {
                    var np = new PostFormat(format.BoardIndex, format.TopicIndex)
                    {
                        DatePosted = DateTime.UtcNow,
                        Message = format.Message,
                        Subject = format.Title,
                        Read = false,
                        Sender = client.Aisling.Username,
                        Recipient = format.To,
                        PostId = (ushort) (boards.Posts.Count + 1)
                    };

                    np.Associate(client.Aisling.Username);
                    boards.Posts.Add(np);
                    ServerContext.SaveCommunityAssets();
                    client.FlushAndSend(new ForumCallback("Message Delivered.", 0x06, true));
                }

                return;
            }

            if (format.Type == 0x04)
            {
                var boards = ServerContext.GlobalBoardCache.Select(i => i.Value)
                    .SelectMany(i => i.Where(n => n.Index == format.BoardIndex))
                    .FirstOrDefault();

                if (boards != null)
                {
                    var np = new PostFormat(format.BoardIndex, format.TopicIndex)
                    {
                        DatePosted = DateTime.UtcNow,
                        Message = format.Message,
                        Subject = format.Title,
                        Read = false,
                        Sender = client.Aisling.Username,
                        PostId = (ushort) (boards.Posts.Count + 1)
                    };

                    np.Associate(client.Aisling.Username);

                    boards.Posts.Add(np);
                    ServerContext.SaveCommunityAssets();
                    client.FlushAndSend(new ForumCallback("Post Added.", 0x06, true));
                }

                return;
            }

            if (format.Type == 0x05)
            {
                var community = ServerContext.GlobalBoardCache.Select(i => i.Value)
                    .SelectMany(i => i.Where(n => n.Index == format.BoardIndex))
                    .FirstOrDefault();

                if (community != null && community.Posts.Count > 0)
                    try
                    {
                        if ((format.BoardIndex == 0
                                ? community.Posts[format.TopicIndex - 1].Recipient
                                : community.Posts[format.TopicIndex - 1].Sender
                            ).Equals(client.Aisling.Username, StringComparison.OrdinalIgnoreCase))
                        {
                            client.FlushAndSend(new ForumCallback("\0", 0x07, true));
                            client.FlushAndSend(new BoardList(ServerContext.Community));
                            client.FlushAndSend(new ForumCallback("Post Deleted.", 0x07, true));

                            community.Posts.RemoveAt(format.TopicIndex - 1);
                            ServerContext.SaveCommunityAssets();

                            client.FlushAndSend(new ForumCallback("Post Deleted.", 0x07, true));
                        }
                        else
                        {
                            client.FlushAndSend(
                                new ForumCallback(ServerContext.Config.CantDoThat, 0x07, true));
                        }
                    }
                    catch (Exception ex)
                    {
                        ServerContext.Logger(ex.Message, Microsoft.Extensions.Logging.LogLevel.Error);
                        ServerContext.Logger(ex.StackTrace, Microsoft.Extensions.Logging.LogLevel.Error);

                        client.FlushAndSend(
                            new ForumCallback(ServerContext.Config.CantDoThat, 0x07, true));
                    }
            }
        }

        protected override void Format3EHandler(GameClient client, ClientFormat3E format)
        {
            #region Sanity Checks

            if (client?.Aisling == null)
                return;

            if (client.Aisling.IsDead())
                return;

            #endregion

            if (client.Aisling.Skulled)
            {
                client.SystemMessage(ServerContext.Config.ReapMessageDuringAction);
                return;
            }

            if (client.Aisling.IsSleeping || client.Aisling.IsFrozen)
            {
                client.Interupt();
                return;
            }

            var skill = client.Aisling.SkillBook.Get(i => i.Slot == format.Index).FirstOrDefault();
            if (skill?.Template == null || skill.Scripts == null)
                return;

            if (!skill.CanUse())
                return;

            skill.InUse = true;

            if (skill.Template.Type == SkillScope.Assail)
                foreach (var assail in client.Aisling.GetAssails())
                {
                    if (assail.Template.Name == skill.Template.Name)
                        continue;

                    ExecuteAbility(client, assail, false);
                }

            foreach (var script in skill.Scripts.Values)
                script.OnUse(client.Aisling);

            if (skill.Template.Cooldown > 0)
                skill.NextAvailableUse = DateTime.UtcNow.AddSeconds(skill.Template.Cooldown);
            else
                skill.NextAvailableUse =
                    DateTime.UtcNow.AddMilliseconds(ServerContext.Config.GlobalBaseSkillDelay);

            skill.InUse = false;
        }

        protected override void Format3FHandler(GameClient client, ClientFormat3F format)
        {
            if (client.Aisling == null || !client.Aisling.LoggedIn)
                return;

            if (ServerContext.GlobalWorldMapTemplateCache.ContainsKey(client.Aisling.World))
            {
                var worldMap = ServerContext.GlobalWorldMapTemplateCache[client.Aisling.World]; 
            
                client.PendingNode = worldMap?.Portals.Find(i => i.Destination.AreaId == format.Index);
            }

            TraverseWorldMap(client, format);

            if (client.Aisling.Abyss)
            {
                client.Aisling.LeaveAbyss(client);
            }
        }


        public static async void TraverseWorldMap(GameClient client, ClientFormat3F format)
        {
            if (!client.MapOpen)
                return;
            
            var selectedPortalNode = client.PendingNode;

            if (selectedPortalNode == null)
                return;

            if (client.Aisling.Abyss)
                await Task.Delay(500).ContinueWith(ct => { client.Aisling.LeaveAbyss(client); }).ConfigureAwait(true);

            for (var i = 0; i < 1; i++)
            {
                client.Send(new ServerFormat33(client.Aisling));
                client.Send(new ServerFormat67());

                client.Aisling.CurrentMapId = selectedPortalNode.Destination.AreaId;
                client.Aisling.X = selectedPortalNode.Destination.Location.X;
                client.Aisling.Y = selectedPortalNode.Destination.Location.Y;
                client.Send(new ServerFormat05(client.Aisling));
            }

            await Task.Delay(500).ContinueWith(ct => { client.RefreshMap(true); }).ConfigureAwait(true);
            client.Refresh();

            if (client.Aisling.Abyss)
                client.UpdateDisplay();

            client.PendingNode = null;
            client.MapOpen = false;
        }

        protected override void Format43Handler(GameClient client, ClientFormat43 format)
        {
            #region Sanity Checks

            if (client?.Aisling == null)
                return;

            #endregion

            if (client.Aisling.Skulled)
            {
                client.SystemMessage(ServerContext.Config.ReapMessageDuringAction);
                return;
            }

            if (format.Type == 3)
            {
                var popTemplate = ServerContext.GlobalPopupCache
                    .OfType<UserClickPopup>().FirstOrDefault(i =>
                        i.X == format.X && i.Y == format.Y && i.MapId == client.Aisling.CurrentMapId);

                if (popTemplate != null)
                {
                    var popup = Popup.Create(client, popTemplate);

                    if (popup != null)
                    {
                        try
                        {
                            if (client.MenuInterpter == null)
                            {
                                CreateInterpreterFromMenuFile(client, popup.Template.YamlKey);

                                if (client.MenuInterpter != null)
                                {
                                    client.MenuInterpter.Start();
                                    client.ShowCurrentMenu(popup, null, client.MenuInterpter.GetCurrentStep());
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            ServerContext.Logger(ex.Message, Microsoft.Extensions.Logging.LogLevel.Error);
                            ServerContext.Logger(ex.StackTrace, Microsoft.Extensions.Logging.LogLevel.Error);
                            client.MenuInterpter = null;
                        }

                        return;
                    }

                    return;
                }
            }

            if (format.Serial == ServerContext.Config.HelperMenuId &&
                ServerContext.GlobalMundaneTemplateCache.ContainsKey(ServerContext.Config
                    .HelperMenuTemplateKey))
            {
                if (client.Aisling.IsSleeping || client.Aisling.IsFrozen)
                    return;

                if (format.Type != 0x01)
                    return;

                var helper = new UserHelper(this, new Mundane
                {
                    Serial = ServerContext.Config.HelperMenuId,
                    Template = ServerContext.GlobalMundaneTemplateCache[
                        ServerContext.Config.HelperMenuTemplateKey]
                });

                helper.OnClick(this, client);
                return;
            }

            if (format.Type == 1)
            {
                var obj = GetObject(client.Aisling.Map, i => i.Serial == format.Serial, Get.All);

                switch (obj)
                {
                    case null:
                        return;

                    case Aisling aisling:
                        client.Aisling.Show(Scope.Self, new ServerFormat34(aisling));
                        break;

                    case Monster monster:
                        var scripts = monster.Scripts?.Values;
                        if (scripts != null)
                            foreach (var script in scripts)
                                script.OnClick(client);
                        break;

                    case Mundane mundane:
                        {
                            try
                            {
                                if (mundane.Scripts != null)
                                    foreach (var script in mundane.Scripts?.Values)
                                        script.OnClick(this, client);

                                CreateInterpreterFromMenuFile(client, mundane.Template.Name, mundane);
                                client.MenuInterpter?.Start();

                                if (client.MenuInterpter != null)
                                    client.ShowCurrentMenu(mundane, null, client.MenuInterpter.GetCurrentStep());
                            }
                            catch (Exception ex)
                            {
                                ServerContext.Logger(ex.Message, Microsoft.Extensions.Logging.LogLevel.Error);
                                ServerContext.Logger(ex.StackTrace, Microsoft.Extensions.Logging.LogLevel.Error);

                            }
                        }
                        break;
                }
            }
        }

        protected override void Format44Handler(GameClient client, ClientFormat44 format)
        {
            #region Sanity Checks

            if (client?.Aisling == null)
                return;

            if (!client.Aisling.LoggedIn)
                return;

            if (client.Aisling.Dead)
                return;

            #endregion

            if (client.Aisling.Skulled)
            {
                client.SystemMessage(ServerContext.Config.ReapMessageDuringAction);
                return;
            }

            if (client.Aisling.IsSleeping || client.Aisling.IsFrozen)
                return;

            if (client.Aisling.EquipmentManager.Equipment.ContainsKey(format.Slot))
                client.Aisling.EquipmentManager?.RemoveFromExisting(format.Slot);
        }

        protected override void Format45Handler(GameClient client, ClientFormat45 format)
        {
            client.LastPingResponse = DateTime.UtcNow;
            AutoSave(client);
        }

        protected override void Format47Handler(GameClient client, ClientFormat47 format)
        {
            #region Sanity Checks

            if (client?.Aisling == null)
                return;

            if (!client.Aisling.LoggedIn)
                return;

            if (client.IsRefreshing)
                return;

            CancelIfCasting(client);

            #endregion

            if (client.Aisling.IsSleeping || client.Aisling.IsFrozen)
                return;

            if (client.Aisling.Skulled)
            {
                client.SystemMessage(ServerContext.Config.ReapMessageDuringAction);
                return;
            }

            var attribute = (Stat) format.Stat;

            if (client.Aisling.StatPoints == 0)
            {
                client.SendMessage(0x02, ServerContext.Config.CantDoThat);
                return;
            }

            if ((attribute & Stat.Str) == Stat.Str)
            {
                client.Aisling._Str++;
                client.SendMessage(0x02, ServerContext.Config.StrAddedMessage);
            }

            if ((attribute & Stat.Int) == Stat.Int)
            {
                client.Aisling._Int++;
                client.SendMessage(0x02, ServerContext.Config.IntAddedMessage);
            }

            if ((attribute & Stat.Wis) == Stat.Wis)
            {
                client.Aisling._Wis++;
                client.SendMessage(0x02, ServerContext.Config.WisAddedMessage);
            }

            if ((attribute & Stat.Con) == Stat.Con)
            {
                client.Aisling._Con++;
                client.SendMessage(0x02, ServerContext.Config.ConAddedMessage);
            }

            if ((attribute & Stat.Dex) == Stat.Dex)
            {
                client.Aisling._Dex++;
                client.SendMessage(0x02, ServerContext.Config.DexAddedMessage);
            }

            if (client.Aisling._Wis > ServerContext.Config.StatCap)
                client.Aisling._Wis = ServerContext.Config.StatCap;
            if (client.Aisling._Str > ServerContext.Config.StatCap)
                client.Aisling._Str = ServerContext.Config.StatCap;
            if (client.Aisling._Int > ServerContext.Config.StatCap)
                client.Aisling._Int = ServerContext.Config.StatCap;
            if (client.Aisling._Con > ServerContext.Config.StatCap)
                client.Aisling._Con = ServerContext.Config.StatCap;
            if (client.Aisling._Dex > ServerContext.Config.StatCap)
                client.Aisling._Dex = ServerContext.Config.StatCap;

            if (client.Aisling._Wis <= 0)
                client.Aisling._Wis = ServerContext.Config.StatCap;
            if (client.Aisling._Str <= 0)
                client.Aisling._Str = ServerContext.Config.StatCap;
            if (client.Aisling._Int <= 0)
                client.Aisling._Int = ServerContext.Config.StatCap;
            if (client.Aisling._Con <= 0)
                client.Aisling._Con = ServerContext.Config.StatCap;
            if (client.Aisling._Dex <= 0)
                client.Aisling._Dex = ServerContext.Config.StatCap;

            if (!client.Aisling.GameMaster)
                client.Aisling.StatPoints--;

            if (client.Aisling.StatPoints < 0)
                client.Aisling.StatPoints = 0;

            client.Aisling.Show(Scope.Self, new ServerFormat08(client.Aisling, StatusFlags.All));
        }

        protected override void Format4AHandler(GameClient client, ClientFormat4A format)
        {
            if (format == null)
                return;

            if (client == null || !client.Aisling.LoggedIn)
                return;

            if (client.Aisling.Skulled)
            {
                client.SystemMessage(ServerContext.Config.ReapMessageDuringAction);
                return;
            }

            var trader = GetObject<Aisling>(client.Aisling.Map, i => i.Serial == format.Id);
            var player = client.Aisling;

            if (player == null || trader == null)
                return;

            if (!player.WithinRangeOf(trader))
                return;

            switch (format.Type)
            {
                case 0x00:
                {
                    player.Exchange = new ExchangeSession(trader);
                    trader.Exchange = new ExchangeSession(player);

                    var packet = new NetworkPacketWriter();
                    packet.Write((byte) 0x42);
                    packet.Write((byte) 0x00);
                    packet.Write((byte) 0x00);
                    packet.Write((uint) trader.Serial);
                    packet.WriteStringA(trader.Username);
                    client.Send(packet);

                    packet = new NetworkPacketWriter();
                    packet.Write((byte) 0x42);
                    packet.Write((byte) 0x00);
                    packet.Write((byte) 0x00);
                    packet.Write((uint) player.Serial);
                    packet.WriteStringA(player.Username);
                    trader.Client.Send(packet);
                }
                    break;

                case 0x01:
                    var slot = format.ItemSlot;
                    var item = client.Aisling.Inventory.FindInSlot(slot);

                    if (player.Exchange == null)
                        return;

                    if (trader.Exchange == null)
                        return;

                    if (player.Exchange.Trader != trader)
                        return;

                    if (trader.Exchange.Trader != player)
                        return;

                    if (player.Exchange.Confirmed)
                        return;

                    if (item?.Template == null)
                        return;

                    if (trader.Exchange != null)
                        if (player.EquipmentManager.RemoveFromInventory(item, true))
                            if (trader.CurrentWeight + item.Template.CarryWeight < trader.MaximumWeight)
                            {
                                player.Exchange.Items.Add(item);
                                player.Exchange.Weight += item.Template.CarryWeight;

                                var packet = new NetworkPacketWriter();
                                packet.Write((byte) 0x42);
                                packet.Write((byte) 0x00);

                                packet.Write((byte) 0x02);
                                packet.Write((byte) 0x00);
                                packet.Write((byte) player.Exchange.Items.Count);
                                packet.Write(item.DisplayImage);
                                packet.Write(item.Color);
                                packet.WriteStringA(item.DisplayName);
                                client.Send(packet);

                                packet = new NetworkPacketWriter();
                                packet.Write((byte) 0x42);
                                packet.Write((byte) 0x00);

                                packet.Write((byte) 0x02);
                                packet.Write((byte) 0x01);
                                packet.Write((byte) player.Exchange.Items.Count);
                                packet.Write(item.DisplayImage);
                                packet.Write(item.Color);
                                packet.WriteStringA(item.DisplayName);
                                trader.Client.Send(packet);
                            }
                            else
                            {
                                trader.Client.SendMessage(0x02, "You can't hold this.");
                                client.SendMessage(0x02, "They can't hold that.");
                            }

                    break;

                case 0x03:
                {
                    if (player.Exchange == null)
                        return;

                    if (trader.Exchange == null)
                        return;

                    if (player.Exchange.Trader != trader)
                        return;

                    if (trader.Exchange.Trader != player)
                        return;

                    if (player.Exchange.Confirmed)
                        return;

                    var gold = format.Gold;

                    if (gold > player.GoldPoints)
                        return;
                    if (player.Exchange.Gold != 0)
                        return;

                    player.GoldPoints -= gold;
                    player.Exchange.Gold = gold;
                    player.Client.SendStats(StatusFlags.StructC);

                    var packet = new NetworkPacketWriter();
                    packet.Write((byte) 0x42);
                    packet.Write((byte) 0x00);

                    packet.Write((byte) 0x03);
                    packet.Write((byte) 0x00);
                    packet.Write((uint) gold);
                    client.Send(packet);

                    packet = new NetworkPacketWriter();
                    packet.Write((byte) 0x42);
                    packet.Write((byte) 0x00);

                    packet.Write((byte) 0x03);
                    packet.Write((byte) 0x01);
                    packet.Write(gold);
                    trader.Client.Send(packet);
                }
                    break;

                case 0x04:
                    if (player.Exchange == null)
                        return;

                    if (trader.Exchange == null)
                        return;

                    if (player.Exchange.Trader != trader)
                        return;

                    if (trader.Exchange.Trader != player)
                        return;

                    player.CancelExchange();
                    break;

                case 0x05:
                {
                    if (player.Exchange == null)
                        return;

                    if (trader.Exchange == null)
                        return;

                    if (player.Exchange.Trader != trader)
                        return;

                    if (trader.Exchange.Trader != player)
                        return;

                    if (player.Exchange.Confirmed)
                        return;

                    player.Exchange.Confirmed = true;

                    if (trader.Exchange.Confirmed)
                        player.FinishExchange();

                    var packet = new NetworkPacketWriter();
                    packet.Write((byte) 0x42);
                    packet.Write((byte) 0x00);

                    packet.Write((byte) 0x05);
                    packet.Write((byte) 0x00);
                    packet.WriteStringA("Trade was completed.");
                    client.Send(packet);

                    packet = new NetworkPacketWriter();
                    packet.Write((byte) 0x42);
                    packet.Write((byte) 0x00);

                    packet.Write((byte) 0x05);
                    packet.Write((byte) 0x01);
                    packet.WriteStringA("Trade was completed.");
                    trader.Client.Send(packet);
                }
                    break;
            }
        }

        protected override void Format4DHandler(GameClient client, ClientFormat4D format)
        {
            #region Sanity Checks

            if (client?.Aisling == null)
                return;

            if (!client.Aisling.LoggedIn)
                return;

            if (client.Aisling.IsDead())
                return;

            #endregion

            client.Aisling.IsCastingSpell = true;

            var lines = format.Lines;

            if (lines <= 0)
            {
                CancelIfCasting(client);
                return;
            }

            if (client.CastStack.Any())
            {
                var info = client.CastStack.Peek();

                if (info != null)
                {
                    info.SpellLines = lines;
                    info.Started = DateTime.UtcNow;
                }
            }
        }

        protected override void Format4EHandler(GameClient client, ClientFormat4E format)
        {
            #region Sanity Checks

            if (client?.Aisling == null)
                return;

            if (!client.Aisling.LoggedIn)
                return;

            if (client.Aisling.IsDead())
                return;

            #endregion

            var chant = format.Message;
            var subject = chant.IndexOf(" Lev", StringComparison.Ordinal);

            if (subject > 0)
            {
                if (subject < 0 || chant.Length <= subject)
                    return;

                if (subject >= 0 && chant.Length > subject)
                {
                    client.Say(chant.Trim());
                }

                return;
            }

            client.Say(chant, 0x02);
        }

        protected override void Format4FHandler(GameClient client, ClientFormat4F format)
        {
            client.Aisling.ProfileMessage = format.Words;
            client.Aisling.PictureData = format.Image;
        }

        protected override void Format75Handler(GameClient client, ClientFormat75 format)
        {
            AutoSave(client);
        }

        protected override void Format79Handler(GameClient client, ClientFormat79 format)
        {
            if (client == null || client.Aisling == null)
                return;

            client.Aisling.ActiveStatus = format.Status;
        }

        protected override void Format7BHandler(GameClient client, ClientFormat7B format)
        {
            if (format.Type == 0x00)
                client.Send(new ServerFormat6F
                {
                    Type = 0x00,
                    Name = format.Name
                });

            if (format.Type == 0x01)
                client.Send(new ServerFormat6F
                {
                    Type = 0x01
                });
        }

        private static void CheckWarpTransitions(GameClient client)
        {
            foreach (var warps in ServerContext.GlobalWarpTemplateCache.Where(warps =>
                warps.ActivationMapId == client.Aisling.CurrentMapId))
                lock (ServerContext.SyncLock)
                {
                    foreach (var _ in warps.Activations.Where(o =>
                        o.Location.X == client.Aisling.XPos && o.Location.Y == client.Aisling.YPos))
                    {

                        if (warps.WarpType == WarpType.Map)
                        {
                            client.WarpTo(warps);
                            break;
                        }

                        if (warps.WarpType != WarpType.World)
                            continue;

                        if (!ServerContext.GlobalWorldMapTemplateCache.ContainsKey(warps.To.PortalKey))
                            return;

                        client.Aisling.PortalSession = new PortalSession
                        {
                            FieldNumber = warps.To.PortalKey
                        };

                        if (client.Aisling.World != warps.To.PortalKey)
                        {
                            if (ServerContext.GlobalWorldMapTemplateCache.ContainsKey(warps.To.PortalKey))
                            {
                                client.Aisling.World = warps.To.PortalKey;
                            }
                        }

                        client.Aisling.PortalSession.TransitionToMap(client);
                        //break;
                    }
                }
        }

        private static void SendMapData(GameClient client)
        {
            void SendMapChunks()
            {
                for (var i = 0; i < client.Aisling.Map.Rows; i++)
                {
                    var response = new ServerFormat3C
                    {
                        Line = (ushort) i,
                        Data = client.Aisling.Map.GetRowData(i)
                    };
                    client.Send(response);
                }
            }

            lock (ServerContext.SyncLock)
            {
                SendMapChunks();
                client.Aisling.Map.OnLoaded();
            }
        }

        private static void ValidateRedirect(GameClient client, dynamic redirect)
        {
            #region
            if (redirect.developer.Value != redirect.player.Value) return;
            client.Aisling.Developer = true;
            client.Aisling.GameMaster = true;
            #endregion
        }

        private void CheckWalkOverPopups(GameClient client)
        {
            var popupTemplates = ServerContext.GlobalPopupCache
                .OfType<UserWalkPopup>().Where(i => i.MapId == client.Aisling.CurrentMapId);

            foreach (var popupTemplate in popupTemplates)
                if (client.Aisling.X == popupTemplate.X && client.Aisling.Y == popupTemplate.Y)
                {
                    popupTemplate.SpriteId = popupTemplate.SpriteId;

                    var popup = Popup.Create(client, popupTemplate);

                    if (popup == null) continue;
                    if (client.MenuInterpter == null)
                    {
                        CreateInterpreterFromMenuFile(client, popup.Template.YamlKey);

                        if (client.MenuInterpter == null)
                            continue;

                        client.MenuInterpter.Start();
                        var next = client.MenuInterpter?.GetCurrentStep();

                        if (next != null)
                            client.ShowCurrentMenu(popup, null, next);
                    }
                }
        }

        public void EnterGame(GameClient client, ClientFormat10 format)
        {
            client.Encryption.Parameters = format.Parameters;
            client.Server = this;

            var player = LoadPlayer(client, format.Name);

            if (player != null)
            {
                player.GameMaster = ServerContext.Config.GameMasters?.Any(n => n.ToLower() == player.Username.ToLower()) ?? false;
                player.Developer  = ServerContext.Config.DevMode;
            }



            player?.Client?.Refresh();
        }

        private void LeaveGame(GameClient client, ClientFormat0B format)
        {
            #region Sanity Checks

            if (client?.Aisling == null)
                return;

            #endregion

            Party.RemovePartyMember(client.Aisling);

            RemoveFromServer(client, format.Type);

            if (format.Type == 1)
            {
                client.FlushAndSend(new ServerFormat4C());
            }
            else if (format.Type == 3)
            {
                client.LastSave = DateTime.UtcNow;
                client.Aisling.Remove();
            }
        }

        public void RemoveFromServer(GameClient client, byte type = 0)
        {
            client.CloseDialog();
            client.Aisling.CancelExchange();

            client.DlgSession = null;
            client.MenuInterpter = null;

            client.Aisling.LastLogged = DateTime.UtcNow;
            client.Aisling.ActiveReactor = null;
            client.Aisling.ActiveSequence = null;
            client.Aisling.Remove(true);
            client.Aisling.LoggedIn = false;

            if (type == 0)
                ExitGame(client);
        }

        public Aisling LoadPlayer(GameClient client, string player)
        {
            var aisling = StorageManager.AislingBucket.Load(player);

            if (aisling != null && ServerContext.Config.GameMasters.Any() &&
                ServerContext.Config.GameMasters.Exists(i => string.Equals(i.ToLower(), player.ToLower(), StringComparison.Ordinal)))
            {
                aisling.GameMaster = true;
            }

            client.Aisling = aisling;

            if (client.Aisling == null)
            {
                client.SendMessage(0x02, "Your have has been corrupted. Please report this bug to lorule staff.");
                base.ClientDisconnected(client);
                return null;
            }

            if (client.Aisling._Str <= 0 || client.Aisling.Ac > 200 || client.Aisling.ExpLevel > 99)
            {
                client.SendMessage(0x02, "Your have has been corrupted. Please report this bug to lorule staff.");
                base.ClientDisconnected(client);
                return null;
            }


            var dupedClients = Clients.Where(i =>
                    i.Aisling != null && aisling != null && i.Aisling.Username == aisling.Username &&
                    i.Aisling.Serial != aisling.Serial)
                .ToArray();

            if (dupedClients.Any())
                foreach (var dupedClient in dupedClients)
                {
                    dupedClient.Aisling?.Remove(true);
                    base.ClientDisconnected(dupedClient);
                }

            lock (Generator.Random)
            {
                client.Aisling.Serial = Generator.GenerateNumber();
            }

            client.Aisling.Client = client;
            client.Aisling.LoggedIn = false;

            client.LastScriptExecuted = DateTime.UtcNow;

            if (client.Aisling.Map != null) client.Aisling.CurrentMapId = client.Aisling.Map.Id;
            client.Aisling.EquipmentManager.Client = client;
            client.Aisling.CurrentWeight = 0;
            client.Aisling.ActiveStatus = ActivityStatus.Awake;

            ServerContext.Logger(client.Aisling.Username + " : " + ServerContext.Config.ServerWelcomeMessage);

            if (client.Aisling.Dead || client.Aisling.CurrentHp <= 0) client.Aisling.WarpToHell();

            var objAisling = client.Load()
                .SendStats(StatusFlags.All)
                .SendMessage(0x02, ServerContext.Config.ServerWelcomeMessage)
                .LoggedIn(true).Aisling;

            if (objAisling != null)
            {
                var playerObjAisling = objAisling;

                if (playerObjAisling.Dead || playerObjAisling.CurrentHp <= 0)
                {
                    playerObjAisling.Flags = AislingFlags.Ghost;
                    playerObjAisling.WarpToHell();
                }
                else
                {
                    if (client.Aisling.CurrentMapId == ServerContext.Config.TransitionZone)
                    {
                        new PortalSession().TransitionToMap(client);
                    }
                }

                return playerObjAisling;
            }

            return null;
        }

        private void MenuInterpter_OnMovedToNextStep(GameClient client, MenuItem previous, MenuItem current)
        {
            if (client.MenuInterpter == null)
                return;

            if (client.MenuInterpter.IsFinished)
                client.MenuInterpter = null;
        }
    }
}