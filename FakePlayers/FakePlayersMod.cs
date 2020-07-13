using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using CommandSystem;
using HarmonyLib;
using MEC;
using Mirror;
using Mirror.LiteNetLib4Mirror;
using SixModLoader.Api.Extensions;
using SixModLoader.Mods;
using UnityEngine;
using Logger = SixModLoader.Logger;

namespace FakePlayers
{
    [Mod("pl.js6pak.FakePlayers")]
    public class FakePlayersMod
    {
        [AutoHarmony]
        public Harmony Harmony { get; set; }

        public static Dictionary<ReferenceHub, List<ReferenceHub>> Pets { get; } = new Dictionary<ReferenceHub, List<ReferenceHub>>();

        [AutoCommandHandler(typeof(RemoteAdminCommandHandler))]
        public class PetCommand : ICommand
        {
            public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
            {
                var ownerRaw = arguments.ElementAtOrDefault(0);
                if (ownerRaw == null)
                {
                    response = "Missing owner";
                    return false;
                }

                var owner = CommandExtensions.MatchPlayers(ownerRaw, sender)[0];

                var pet = UnityEngine.Object.Instantiate(LiteNetLib4MirrorNetworkManager.singleton.playerPrefab, owner.playerMovementSync.RealModelPosition, Quaternion.identity);
                var hub = ReferenceHub.GetHub(pet);

                foreach (var component in pet.GetComponents<Behaviour>())
                {
                    if (component is ReferenceHub || component is CharacterClassManager || component is PlayerMovementSync || component is PlayerPositionManager)
                        continue;

                    component.enabled = false;
                }

                var roleRaw = arguments.ElementAtOrDefault(1);
                if (roleRaw == null || !Enum.TryParse<RoleType>(roleRaw, true, out var role))
                {
                    response = $"Missing role ({Enum.GetNames(typeof(RoleType)).Join()})";
                    return false;
                }

                hub.characterClassManager.NetworkCurClass = role;

                pet.transform.localScale = new Vector3(0.5f, 0.5f, 0.5f);

                Timing.CallDelayed(1f, () =>
                {
                    hub.nicknameSync.MyNick = $"{owner.nicknameSync.MyNick}'s pet";
                    NetworkServer.Spawn(pet);
                });

                var pets = Pets[owner] = Pets.GetValueSafe(owner) ?? new List<ReferenceHub>();
                pets.Add(hub);

                hub.characterClassManager.netIdentity.isLocalPlayer = true;
                hub.characterClassManager.netIdentity.connectionToClient = hub.characterClassManager.netIdentity.connectionToServer = NetworkServer.localConnection;
                hub.queryProcessor.PlayerId = -1 - Pets.Values.Sum(x => x.Count);

                hub.playerMovementSync._realModelPosition = owner.playerMovementSync.RealModelPosition;

                response = "Created pet";
                return false;
            }

            public string Command => "pet";
            public string[] Aliases => new string[0];
            public string Description => "Creates pet :O";
        }

        [HarmonyPatch(typeof(PlayerMovementSync), nameof(PlayerMovementSync.RealModelPosition), MethodType.Setter)]
        public static class PositionPatch
        {
            public static void Postfix(PlayerMovementSync __instance)
            {
                try
                {
                    if (Pets.TryGetValue(__instance._hub, out var pets))
                    {
                        var position = __instance.RealModelPosition;

                        Timing.CallDelayed(0.25f, () =>
                        {
                            foreach (var pet in pets)
                            {
                                var rotation = Quaternion.LookRotation(__instance.RealModelPosition - pet.playerMovementSync._realModelPosition);
                                pet.playerMovementSync.Rotations = new Vector2(rotation.eulerAngles.x, rotation.eulerAngles.y);

                                if (Vector3.Distance(position, __instance.RealModelPosition) <= 0.5)
                                    continue;

                                pet.playerMovementSync._realModelPosition = position;
                            }
                        });
                    }
                }
                catch (Exception e)
                {
                    Logger.Error(e);
                }
            }
        }

        [HarmonyPatch(typeof(PlayerPositionManager), nameof(PlayerPositionManager.TransmitData))]
        public class TransmitDataPatch
        {
            private static readonly FieldInfo f_players = AccessTools.Field(typeof(PlayerManager), nameof(PlayerManager.players));
            private static readonly MethodInfo m_GetPlayers = AccessTools.Method(typeof(TransmitDataPatch), nameof(GetPlayers));

            public static List<GameObject> GetPlayers()
            {
                return PlayerManager.players.Concat(Pets.Values.SelectMany(x => x).Select(x => x.gameObject)).ToList();
            }

            public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var codeInstructions = instructions.ToList();

                var index = codeInstructions
                    .FindIndex(x => x.opcode == OpCodes.Ldsfld && (FieldInfo) x.operand == f_players);

                codeInstructions.RemoveAt(index);
                codeInstructions.Insert(index, new CodeInstruction(OpCodes.Call, m_GetPlayers));

                return codeInstructions;
            }
        }
    }
}