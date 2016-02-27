﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using osuBancho.Database.Interfaces;
using osuBancho.Helpers;
using osuBancho.IRC.Objects;

namespace osuBancho.Core.Players
{
    static class PlayerManager
    {
        private static readonly ConcurrentDictionary<string, Player> PlayersByToken = new ConcurrentDictionary<string, Player>();
        private static readonly ConcurrentDictionary<int, Player> PlayersById = new ConcurrentDictionary<int, Player>();

        public static IEnumerable<int> PlayersIds
        {
            get { return PlayersById.Select(item => item.Key); } //.Keys; } //TODO: Is this a good way to do this?
        }

        public static IEnumerable<Player> Players
        {
            get { return PlayersById.Select(item => item.Value); } //.Values; } //TODO: Is this a good way to do this?
        }

        public static int PlayersCount
        {
            get { return PlayersById.Skip(0).Count(); }
        }

        public static Player GetPlayerBySessionToken(string token)
        {
            Player player;
            if (PlayersByToken.TryGetValue(token, out player)) return player;
            return null;
        }

        public static Player GetPlayerById(int id)
        {
            Player player;
            if (PlayersById.TryGetValue(id, out player)) return player;
            return null;
        }

        public static Player GetPlayerByUsername(string username)
        {
            return Players.FirstOrDefault(player => player.Username == username);
        }

        //TODO?: do worker things on a threaded OnCycle method, to kill zombies and update others things like onlines now (or onlines now is better in a separated worker thread?)

        public static void QueueCommandForAll(Commands command, object serializable)
        {
            foreach (Player player in Players)
            {
                player.QueueCommand(command, serializable);
            }
        }

        public static void QueueCommandForAll(Commands command, object serializable, int exclude)
        {
            foreach (Player player in Players)
            {
                if (player.Id == exclude) continue;
                player.QueueCommand(command, serializable);
            }
        }

        public static bool AuthenticatePlayer(string username, string passHash, out Player player)
        {
            DataRow playerData;
            using (IQueryAdapter dbClient = Bancho.DatabaseManager.GetQueryReactor())
            {
                dbClient.SetQuery("SELECT * " +
                                  "FROM users_info " +
                                  "WHERE username = @name AND " +
                                  "password = @password LIMIT 1");

                dbClient.AddParameter("name", username);
                dbClient.AddParameter("password", passHash);
                playerData = dbClient.getRow();
            }
            if (playerData == null)
            {
                player = null;
                return false;
            }

            player = new Player(Guid.NewGuid().ToString(), playerData);
            if (Bancho.IsRestricted && !(player.Tags.HasFlag(UserTags.Admin) || player.Tags.HasFlag(UserTags.Peppy)))
            {
                throw new CanNotAccessBanchoException();
            }

            if (!PlayersById.TryAdd(player.Id, player))
            {
                DisconnectPlayer(player.Id, DisconnectReason.Kick); //TODO?: Use TryUpdate?
                if (!PlayersById.TryAdd(player.Id, player)) //TODO?: should i implement an temporary ip ban to floods of login?
                    throw new Exception("Can't disconnect the another player!");
            }
            PlayersByToken.TryAdd(player.Token, player);

            //TODO: Improve this?
            QueueCommandForAll(Commands.UserForLoad, player.Id, player.Id); //NOTE: Osu automatic logout when see that an user with same id has logged in

            return true;
        }

        public static void DisconnectPlayer(int playerId, DisconnectReason reason)
        {
            Player player;
            if (!PlayersById.TryRemove(playerId, out player)) return;
            PlayersByToken.TryRemove(player.Token, out player);

            player.OnDisconnected();

            //TODO: Improve this?
            QueueCommandForAll((Commands)12, new bIRCQuit(playerId, bIRCQuit.Enum1.const_0));

            Debug.WriteLine("{0} has disconnected ({1})", player.Username, reason);
        }

        public static async Task<bool> OnPacketReceived(string Token, Stream receivedStream, MemoryStream outStream)
        {
            if (receivedStream.Length < 7)
            {
                return false;
            }
            Player player = GetPlayerBySessionToken(Token);

            if (player != null)
            {
                receivedStream.Position = 0;
               
                player.OnPacketReceived(receivedStream);
                player.SerializeCommands(outStream);
                
                return true;
            }
            return false;
        }
    }
}