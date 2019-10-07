/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using log4net;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenMetaverse.Messages.Linden;
using Mono.Addins;
using OpenSim.Framework;
using OpenSim.Framework.Capabilities;
using OpenSim.Framework.Console;
using OpenSim.Framework.Servers;
using OpenSim.Framework.Monitoring;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Services.Interfaces;
using Caps = OpenSim.Framework.Capabilities.Caps;
using GridRegion = OpenSim.Services.Interfaces.GridRegion;

namespace OpenSim.Region.CoreModules.World.Land
{
    // used for caching
    internal class ExtendedLandData
    {
        public LandData LandData;
        public ulong RegionHandle;
        public uint X, Y;
        public byte RegionAccess;
    }

    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "LandManagementModule")]
    public class LandManagementModule : INonSharedRegionModule
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly string LogHeader = "[LAND MANAGEMENT MODULE]";

        /// <summary>
        /// Minimum land unit size in region co-ordinates.
        /// </summary>

        public const int LandUnit = 4;

        private LandChannel landChannel;
        private Scene m_scene;

        protected IGroupsModule m_groupManager;
        protected IUserManagement m_userManager;
        protected IPrimCountModule m_primCountModule;
        protected IDialogModule m_Dialog;

        /// <value>
        /// Local land ids at specified region co-ordinates (region size / 4)
        /// </value>
        private int[,] m_landIDList;

        /// <value>
        /// Land objects keyed by local id
        /// </value>
//        private readonly Dictionary<int, ILandObject> m_landList = new Dictionary<int, ILandObject>();

        //ubit: removed the readonly so i can move it around
        private Dictionary<int, ILandObject> m_landList = new Dictionary<int, ILandObject>();
        private Dictionary<UUID, int> m_landUUIDList = new Dictionary<UUID, int>();

        private int m_lastLandLocalID = LandChannel.START_LAND_LOCAL_ID - 1;

        private bool m_allowedForcefulBans = true;
        private bool m_showBansLines = true;
        private UUID DefaultGodParcelGroup;
        private string DefaultGodParcelName;
        private UUID DefaultGodParcelOwner;

        // caches ExtendedLandData
        private Cache parcelInfoCache;

        /// <summary>
        /// Record positions that avatar's are currently being forced to move to due to parcel entry restrictions.
        /// </summary>
        private HashSet<UUID> forcedPosition = new HashSet<UUID>();


        // Enables limiting parcel layer info transmission when doing simple updates
        private bool shouldLimitParcelLayerInfoToViewDistance { get; set; }
        // "View distance" for sending parcel layer info if asked for from a view point in the region
        private int parcelLayerViewDistance { get; set; }

        private float m_BanLineSafeHeight = 100.0f;
        public float BanLineSafeHeight
        {
            get { return m_BanLineSafeHeight; }
            private set
            {
                if (value > 20f && value <= 5000f)
                    m_BanLineSafeHeight = value;
                else
                    m_BanLineSafeHeight = 100.0f;
            }
        }

        #region INonSharedRegionModule Members

        public Type ReplaceableInterface
        {
            get { return null; }
        }

        public void Initialise(IConfigSource source)
        {
            shouldLimitParcelLayerInfoToViewDistance = true;
            parcelLayerViewDistance = 128;
            IConfig landManagementConfig = source.Configs["LandManagement"];
            if (landManagementConfig != null)
            {
                shouldLimitParcelLayerInfoToViewDistance = landManagementConfig.GetBoolean("LimitParcelLayerUpdateDistance", shouldLimitParcelLayerInfoToViewDistance);
                parcelLayerViewDistance = landManagementConfig.GetInt("ParcelLayerViewDistance", parcelLayerViewDistance);
                DefaultGodParcelGroup = new UUID(landManagementConfig.GetString("DefaultAdministratorGroupUUID", UUID.Zero.ToString()));
                DefaultGodParcelName = landManagementConfig.GetString("DefaultAdministratorParcelName", "Admin Parcel");
                DefaultGodParcelOwner = new UUID(landManagementConfig.GetString("DefaultAdministratorOwnerUUID", UUID.Zero.ToString()));
                bool disablebans = landManagementConfig.GetBoolean("DisableParcelBans", !m_allowedForcefulBans);
                m_allowedForcefulBans = !disablebans;
                m_showBansLines = landManagementConfig.GetBoolean("ShowParcelBansLines", m_showBansLines);
                m_BanLineSafeHeight = landManagementConfig.GetFloat("BanLineSafeHeight", m_BanLineSafeHeight);
            }
        }

        public void AddRegion(Scene scene)
        {
            m_scene = scene;
            m_landIDList = new int[m_scene.RegionInfo.RegionSizeX / LandUnit, m_scene.RegionInfo.RegionSizeY / LandUnit];

            landChannel = new LandChannel(scene, this);

            parcelInfoCache = new Cache();
            parcelInfoCache.Size = 30; // the number of different parcel requests in this region to cache
            parcelInfoCache.DefaultTTL = new TimeSpan(0, 5, 0);

            m_scene.EventManager.OnObjectAddedToScene += EventManagerOnParcelPrimCountAdd;
            m_scene.EventManager.OnParcelPrimCountAdd += EventManagerOnParcelPrimCountAdd;

            m_scene.EventManager.OnObjectBeingRemovedFromScene += EventManagerOnObjectBeingRemovedFromScene;
            m_scene.EventManager.OnParcelPrimCountUpdate += EventManagerOnParcelPrimCountUpdate;
            m_scene.EventManager.OnRequestParcelPrimCountUpdate += EventManagerOnRequestParcelPrimCountUpdate;

            m_scene.EventManager.OnAvatarEnteringNewParcel += EventManagerOnAvatarEnteringNewParcel;
            m_scene.EventManager.OnClientMovement += EventManagerOnClientMovement;
            m_scene.EventManager.OnValidateLandBuy += EventManagerOnValidateLandBuy;
            m_scene.EventManager.OnLandBuy += EventManagerOnLandBuy;
            m_scene.EventManager.OnNewClient += EventManagerOnNewClient;
            m_scene.EventManager.OnMakeChildAgent += EventMakeChildAgent;
            m_scene.EventManager.OnSignificantClientMovement += EventManagerOnSignificantClientMovement;
            m_scene.EventManager.OnNoticeNoLandDataFromStorage += EventManagerOnNoLandDataFromStorage;
            m_scene.EventManager.OnIncomingLandDataFromStorage += EventManagerOnIncomingLandDataFromStorage;
            m_scene.EventManager.OnSetAllowForcefulBan += EventManagerOnSetAllowedForcefulBan;
            m_scene.EventManager.OnRegisterCaps += EventManagerOnRegisterCaps;

            lock (m_scene)
            {
                m_scene.LandChannel = (ILandChannel)landChannel;
            }

            RegisterCommands();
        }

        public void RegionLoaded(Scene scene)
        {
            m_userManager = m_scene.RequestModuleInterface<IUserManagement>();
            m_groupManager = m_scene.RequestModuleInterface<IGroupsModule>();
            m_primCountModule = m_scene.RequestModuleInterface<IPrimCountModule>();
            m_Dialog = m_scene.RequestModuleInterface<IDialogModule>();
        }

        public void RemoveRegion(Scene scene)
        {
            // TODO: Release event manager listeners here
        }

//        private bool OnVerifyUserConnection(ScenePresence scenePresence, out string reason)
//        {
//            ILandObject nearestParcel = m_scene.GetNearestAllowedParcel(scenePresence.UUID, scenePresence.AbsolutePosition.X, scenePresence.AbsolutePosition.Y);
//            reason = "You are not allowed to enter this sim.";
//            return nearestParcel != null;
//        }

        void EventManagerOnNewClient(IClientAPI client)
        {
            //Register some client events
            client.OnParcelPropertiesRequest += ClientOnParcelPropertiesRequest;
            client.OnParcelDivideRequest += ClientOnParcelDivideRequest;
            client.OnParcelJoinRequest += ClientOnParcelJoinRequest;
            client.OnParcelPropertiesUpdateRequest += ClientOnParcelPropertiesUpdateRequest;
            client.OnParcelSelectObjects += ClientOnParcelSelectObjects;
            client.OnParcelObjectOwnerRequest += ClientOnParcelObjectOwnerRequest;
            client.OnParcelAccessListRequest += ClientOnParcelAccessListRequest;
            client.OnParcelAccessListUpdateRequest += ClientOnParcelAccessListUpdateRequest;
            client.OnParcelAbandonRequest += ClientOnParcelAbandonRequest;
            client.OnParcelGodForceOwner += ClientOnParcelGodForceOwner;
            client.OnParcelReclaim += ClientOnParcelReclaim;
            client.OnParcelInfoRequest += ClientOnParcelInfoRequest;
            client.OnParcelDeedToGroup += ClientOnParcelDeedToGroup;
            client.OnParcelEjectUser += ClientOnParcelEjectUser;
            client.OnParcelFreezeUser += ClientOnParcelFreezeUser;
            client.OnSetStartLocationRequest += ClientOnSetHome;
            client.OnParcelBuyPass += ClientParcelBuyPass;
            client.OnParcelGodMark += ClientOnParcelGodMark;
        }

        public void EventMakeChildAgent(ScenePresence avatar)
        {
            avatar.currentParcelUUID = UUID.Zero;
        }

        public void Close()
        {
        }

        public string Name
        {
            get { return "LandManagementModule"; }
        }

        #endregion

        #region Parcel Add/Remove/Get/Create

        public void EventManagerOnSetAllowedForcefulBan(bool forceful)
        {
            AllowedForcefulBans = forceful;
        }

        public void UpdateLandObject(int local_id, LandData data)
        {
            LandData newData = data.Copy();
            newData.LocalID = local_id;

            ILandObject land;
            lock (m_landList)
            {
                if (m_landList.TryGetValue(local_id, out land))
                {
                    land.LandData = newData;
                    m_landUUIDList[newData.GlobalID] = local_id;
                }
            }

            if (land != null)
                m_scene.EventManager.TriggerLandObjectUpdated((uint)local_id, land);
        }

        public bool AllowedForcefulBans
        {
            get { return m_allowedForcefulBans; }
            set { m_allowedForcefulBans = value; }
        }

        /// <summary>
        /// Resets the sim to the default land object (full sim piece of land owned by the default user)
        /// </summary>
        public void ResetSimLandObjects()
        {
            //Remove all the land objects in the sim and add a blank, full sim land object set to public
            lock (m_landList)
            {
                foreach(ILandObject parcel in m_landList.Values)
                    parcel.Clear();

                m_landList.Clear();
                m_landUUIDList.Clear();
                m_lastLandLocalID = LandChannel.START_LAND_LOCAL_ID - 1;

                m_landIDList = new int[m_scene.RegionInfo.RegionSizeX / LandUnit, m_scene.RegionInfo.RegionSizeY / LandUnit];
            }
        }

        /// <summary>
        /// Create a default parcel that spans the entire region and is owned by the estate owner.
        /// </summary>
        /// <returns>The parcel created.</returns>
        protected ILandObject CreateDefaultParcel()
        {
            m_log.DebugFormat("{0} Creating default parcel for region {1}", LogHeader, m_scene.RegionInfo.RegionName);

            ILandObject fullSimParcel = new LandObject(UUID.Zero, false, m_scene);

            fullSimParcel.SetLandBitmap(fullSimParcel.GetSquareLandBitmap(0, 0,
                                            (int)m_scene.RegionInfo.RegionSizeX, (int)m_scene.RegionInfo.RegionSizeY));
            LandData ldata = fullSimParcel.LandData;
            ldata.SimwideArea = ldata.Area;
            ldata.OwnerID = m_scene.RegionInfo.EstateSettings.EstateOwner;
            ldata.ClaimDate = Util.UnixTimeSinceEpoch();

            return AddLandObject(fullSimParcel);
        }

        public List<ILandObject> AllParcels()
        {
            lock (m_landList)
            {
                return new List<ILandObject>(m_landList.Values);
            }
        }

        public List<ILandObject> ParcelsNearPoint(Vector3 position)
        {
            List<ILandObject> parcelsNear = new List<ILandObject>();
            for (int x = -8; x <= 8; x += 4)
            {
                for (int y = -8; y <= 8; y += 4)
                {
                    ILandObject check = GetLandObject(position.X + x, position.Y + y);
                    if (check != null)
                    {
                        if (!parcelsNear.Contains(check))
                        {
                            parcelsNear.Add(check);
                        }
                    }
                }
            }

            return parcelsNear;
        }

        // checks and enforces bans or restrictions
        // returns true if enforced
        public bool EnforceBans(ILandObject land, ScenePresence avatar)
        {
            Vector3 agentpos = avatar.AbsolutePosition;
            float h = m_scene.GetGroundHeight(agentpos.X, agentpos.Y) + m_scene.LandChannel.BanLineSafeHeight;
            float zdif = avatar.AbsolutePosition.Z - h;
            if (zdif > 0 )
            {
                forcedPosition.Remove(avatar.UUID);
                avatar.lastKnownAllowedPosition = agentpos;
                return false;
            }

            bool ban = false;
            string reason = "";
            if (land.IsRestrictedFromLand(avatar.UUID))
            {
                reason = "You do not have access to the parcel";
                ban = true;
            }

            if (land.IsBannedFromLand(avatar.UUID))
            {
                if ( m_allowedForcefulBans)
                {
                   reason ="You are banned from parcel";
                   ban = true;
                }
                else if(!ban)
                {
                    if (forcedPosition.Contains(avatar.UUID))
                        avatar.ControllingClient.SendAlertMessage("You are banned from parcel, please leave by your own will");
                    forcedPosition.Remove(avatar.UUID);
                    avatar.lastKnownAllowedPosition = agentpos;
                    return false;
                }
            }

            if(ban)
            {
                if (!forcedPosition.Contains(avatar.UUID))
                    avatar.ControllingClient.SendAlertMessage(reason);

                if(zdif > -4f)
                {

                    agentpos.Z = h + 4.0f;
                    ForceAvatarToPosition(avatar, agentpos);
                    return true;
                }

                if (land.ContainsPoint((int)avatar.lastKnownAllowedPosition.X,
                            (int) avatar.lastKnownAllowedPosition.Y))
                {
                    Vector3? pos = m_scene.GetNearestAllowedPosition(avatar);
                    if (pos == null)
                    {
                         forcedPosition.Remove(avatar.UUID);
                         m_scene.TeleportClientHome(avatar.UUID, avatar.ControllingClient);
                    }
                    else
                        ForceAvatarToPosition(avatar, (Vector3)pos);
                }
                else
                {
                    ForceAvatarToPosition(avatar, avatar.lastKnownAllowedPosition);
                }
                return true;
            }
            else
            {
                forcedPosition.Remove(avatar.UUID);
                avatar.lastKnownAllowedPosition = agentpos;
                return false;
            }
        }

        private void ForceAvatarToPosition(ScenePresence avatar, Vector3? position)
        {
            if (m_scene.Permissions.IsGod(avatar.UUID)) return;

            if (!position.HasValue)
                return;

            if(avatar.MovingToTarget)
                avatar.ResetMoveToTarget();
            avatar.AbsolutePosition = position.Value;
            avatar.lastKnownAllowedPosition = position.Value;
            avatar.Velocity = Vector3.Zero;
            if(avatar.IsSatOnObject)
                avatar.StandUp();
            forcedPosition.Add(avatar.UUID);
        }

        public void EventManagerOnAvatarEnteringNewParcel(ScenePresence avatar, int localLandID, UUID regionID)
        {
            if (m_scene.RegionInfo.RegionID == regionID)
            {
                ILandObject parcelAvatarIsEntering;
                lock (m_landList)
                {
                    parcelAvatarIsEntering = m_landList[localLandID];
                }

                if (parcelAvatarIsEntering != null &&
                    avatar.currentParcelUUID != parcelAvatarIsEntering.LandData.GlobalID)
                {
                    SendLandUpdate(avatar, parcelAvatarIsEntering);
                    avatar.currentParcelUUID = parcelAvatarIsEntering.LandData.GlobalID;
                    EnforceBans(parcelAvatarIsEntering, avatar);
                }
            }
        }

        public void SendOutNearestBanLine(IClientAPI client)
        {
            ScenePresence sp = m_scene.GetScenePresence(client.AgentId);
            if (sp == null || sp.IsDeleted)
                return;

            List<ILandObject> checkLandParcels = ParcelsNearPoint(sp.AbsolutePosition);
            foreach (ILandObject checkBan in checkLandParcels)
            {
                if (checkBan.IsBannedFromLand(client.AgentId))
                {
                    checkBan.SendLandProperties((int)ParcelPropertiesStatus.CollisionBanned, false, (int)ParcelResult.Single, client);
                    return; //Only send one
                }
                if (checkBan.IsRestrictedFromLand(client.AgentId))
                {
                    checkBan.SendLandProperties((int)ParcelPropertiesStatus.CollisionNotOnAccessList, false, (int)ParcelResult.Single, client);
                    return; //Only send one
                }
            }
            return;
        }

        public void sendClientInitialLandInfo(IClientAPI remoteClient, bool overlay)
        {
            ScenePresence avatar;

            if (!m_scene.TryGetScenePresence(remoteClient.AgentId, out avatar))
                return;

            if (!avatar.IsChildAgent)
            {
                ILandObject over = GetLandObject(avatar.AbsolutePosition.X, avatar.AbsolutePosition.Y);
                if (over == null)
                    return;

                avatar.currentParcelUUID = over.LandData.GlobalID;
                over.SendLandUpdateToClient(avatar.ControllingClient);
            }
            if(overlay)
                SendParcelOverlay(remoteClient);
        }

        public void SendLandUpdate(ScenePresence avatar, ILandObject over)
        {
            if (avatar.IsChildAgent)
                return;

            if (over != null)
            {
                   over.SendLandUpdateToClient(avatar.ControllingClient);
// sl doesnt seem to send this now, as it used 2
//                    SendParcelOverlay(avatar.ControllingClient);
            }
        }

        public void EventManagerOnSignificantClientMovement(ScenePresence avatar)
        {
            if (avatar.IsChildAgent)
                return;

            if ( m_allowedForcefulBans && m_showBansLines)
                SendOutNearestBanLine(avatar.ControllingClient);
        }

        /// <summary>
        /// Like handleEventManagerOnSignificantClientMovement, but called with an AgentUpdate regardless of distance.
        /// </summary>
        /// <param name="avatar"></param>
        public void EventManagerOnClientMovement(ScenePresence avatar)
        {
            if (avatar.IsChildAgent)
                return;

            Vector3 pos = avatar.AbsolutePosition;
            ILandObject over = GetLandObject(pos.X, pos.Y);
            if (over != null)
            {
                EnforceBans(over, avatar);
                pos = avatar.AbsolutePosition;
                ILandObject newover = GetLandObject(pos.X, pos.Y);
                if(over != newover || avatar.currentParcelUUID != newover.LandData.GlobalID)
                {
                    m_scene.EventManager.TriggerAvatarEnteringNewParcel(avatar,
                            newover.LandData.LocalID, m_scene.RegionInfo.RegionID);
                }
            }
        }

        public void ClientParcelBuyPass(IClientAPI remote_client, UUID targetID, int landLocalID)
        {
            ILandObject land;
            lock (m_landList)
            {
                m_landList.TryGetValue(landLocalID, out land);
            }
            // trivial checks
            if(land == null)
                return;

            LandData ldata = land.LandData;

            if(ldata == null)
                return;

            if(ldata.OwnerID == targetID)
                return;

            if(ldata.PassHours == 0)
                return;

            // don't allow passes on group owned until we can give money to groups
            if(ldata.IsGroupOwned)
            {
                remote_client.SendAgentAlertMessage("pass to group owned parcel not suported", false);
                return;
            }

            if((ldata.Flags & (uint)ParcelFlags.UsePassList) == 0)
                return;

            int cost = ldata.PassPrice;

            int idx = land.LandData.ParcelAccessList.FindIndex(
                delegate(LandAccessEntry e)
                {
                    if (e.AgentID == targetID && e.Flags == AccessList.Access)
                        return true;
                    return false;
                });
            int now = Util.UnixTimeSinceEpoch();
            int expires = (int)(3600.0 * ldata.PassHours + 0.5f);
            int currenttime = -1;
            if (idx != -1)
            {
                if(ldata.ParcelAccessList[idx].Expires == 0)
                {
                    remote_client.SendAgentAlertMessage("You already have access to parcel", false);
                    return;
                }

                currenttime = ldata.ParcelAccessList[idx].Expires - now;
                if(currenttime > (int)(0.25f * expires + 0.5f))
                {
                    if(currenttime > 3600)
                        remote_client.SendAgentAlertMessage(string.Format("You already have a pass valid for {0:0.###} hours",
                                    currenttime/3600f), false);
                   else if(currenttime > 60)
                        remote_client.SendAgentAlertMessage(string.Format("You already have a pass valid for {0:0.##} minutes",
                                    currenttime/60f), false);
                   else
                        remote_client.SendAgentAlertMessage(string.Format("You already have a pass valid for {0:0.#} seconds",
                                    currenttime), false);
                    return;
                }
            }
            
            LandAccessEntry entry = new LandAccessEntry();
            entry.AgentID = targetID;
            entry.Flags = AccessList.Access;
            entry.Expires = now + expires;
            if(currenttime > 0)
                entry.Expires += currenttime;
            IMoneyModule mm = m_scene.RequestModuleInterface<IMoneyModule>();
            if(cost != 0 && mm != null)
            {
                WorkManager.RunInThreadPool(
                delegate
                {
                    string regionName = m_scene.RegionInfo.RegionName;

                    if (!mm.AmountCovered(remote_client.AgentId, cost))
                    {
                        remote_client.SendAgentAlertMessage(String.Format("Insufficient funds in region '{0}' money system", regionName), true); 
                        return;
                    }

                    string payDescription = String.Format("Parcel '{0}' at region '{1} {2:0.###} hours access pass", ldata.Name, regionName, ldata.PassHours);

                    if(!mm.MoveMoney(remote_client.AgentId, ldata.OwnerID, cost,MoneyTransactionType.LandPassSale, payDescription))
                    {
                        remote_client.SendAgentAlertMessage("Sorry pass payment processing failed, please try again later", true); 
                        return;
                    }

                    if (idx != -1)
                        ldata.ParcelAccessList.RemoveAt(idx);
                    ldata.ParcelAccessList.Add(entry);
                    m_scene.EventManager.TriggerLandObjectUpdated((uint)land.LandData.LocalID, land);
                    return;
                }, null, "ParcelBuyPass");
            }
            else
            {
                if (idx != -1)
                    ldata.ParcelAccessList.RemoveAt(idx);
                ldata.ParcelAccessList.Add(entry);
                m_scene.EventManager.TriggerLandObjectUpdated((uint)land.LandData.LocalID, land);
            }
        }

        public void ClientOnParcelAccessListRequest(UUID agentID, UUID sessionID, uint flags, int sequenceID,
                                                    int landLocalID, IClientAPI remote_client)
        {
            ILandObject land;
            lock (m_landList)
            {
                m_landList.TryGetValue(landLocalID, out land);
            }

            if (land != null)
            {
                land.SendAccessList(agentID, sessionID, flags, sequenceID, remote_client);
            }
        }

        public void ClientOnParcelAccessListUpdateRequest(UUID agentID,
                uint flags, UUID transactionID, int landLocalID, List<LandAccessEntry> entries,
                IClientAPI remote_client)
        {
            if ((flags & 0x03) == 0)
                return; // we only have access and ban

            ILandObject land;
            lock (m_landList)
            {
                m_landList.TryGetValue(landLocalID, out land);
            }

            if (land != null)
            {
                GroupPowers requiredPowers = GroupPowers.None;
                if ((flags & (uint)AccessList.Access) != 0)
                    requiredPowers |= GroupPowers.LandManageAllowed;
                if ((flags & (uint)AccessList.Ban) != 0)
                    requiredPowers |= GroupPowers.LandManageBanned;

                if(requiredPowers == GroupPowers.None)
                    return;

                if (m_scene.Permissions.CanEditParcelProperties(agentID,
                        land, requiredPowers, false))
                {
                    land.UpdateAccessList(flags, transactionID, entries);
                }
            }
            else
            {
                m_log.WarnFormat("[LAND MANAGEMENT MODULE]: Invalid local land ID {0}", landLocalID);
            }
        }

        /// <summary>
        /// Adds a land object to the stored list and adds them to the landIDList to what they own
        /// </summary>
        /// <param name="new_land">
        /// The land object being added.
        /// Will return null if this overlaps with an existing parcel that has not had its bitmap adjusted.
        /// </param>
        public ILandObject AddLandObject(ILandObject new_land)
        {
            // Only now can we add the prim counts to the land object - we rely on the global ID which is generated
            // as a random UUID inside LandData initialization
            if (m_primCountModule != null)
                new_land.PrimCounts = m_primCountModule.GetPrimCounts(new_land.LandData.GlobalID);

            lock (m_landList)
            {
                int newLandLocalID = m_lastLandLocalID + 1;
                new_land.LandData.LocalID = newLandLocalID;

                bool[,] landBitmap = new_land.GetLandBitmap();
                if (landBitmap.GetLength(0) != m_landIDList.GetLength(0) || landBitmap.GetLength(1) != m_landIDList.GetLength(1))
                {
                    // Going to variable sized regions can cause mismatches
                    m_log.ErrorFormat("{0} AddLandObject. Added land bitmap different size than region ID map. bitmapSize=({1},{2}), landIDSize=({3},{4})",
                        LogHeader, landBitmap.GetLength(0), landBitmap.GetLength(1), m_landIDList.GetLength(0), m_landIDList.GetLength(1));
                }
                else
                {
                    // If other land objects still believe that they occupy any parts of the same space,
                    // then do not allow the add to proceed.
                    for (int x = 0; x < landBitmap.GetLength(0); x++)
                    {
                        for (int y = 0; y < landBitmap.GetLength(1); y++)
                        {
                            if (landBitmap[x, y])
                            {
                                int lastRecordedLandId = m_landIDList[x, y];

                                if (lastRecordedLandId > 0)
                                {
                                    ILandObject lastRecordedLo = m_landList[lastRecordedLandId];

                                    if (lastRecordedLo.LandBitmap[x, y])
                                    {
                                        m_log.ErrorFormat(
                                            "{0}: Cannot add parcel \"{1}\", local ID {2} at tile {3},{4} because this is still occupied by parcel \"{5}\", local ID {6} in {7}",
                                            LogHeader, new_land.LandData.Name, new_land.LandData.LocalID, x, y,
                                            lastRecordedLo.LandData.Name, lastRecordedLo.LandData.LocalID, m_scene.Name);

                                        return null;
                                    }
                                }
                            }
                        }
                    }

                    for (int x = 0; x < landBitmap.GetLength(0); x++)
                    {
                        for (int y = 0; y < landBitmap.GetLength(1); y++)
                        {
                            if (landBitmap[x, y])
                            {
                                //                            m_log.DebugFormat(
                                //                                "[LAND MANAGEMENT MODULE]: Registering parcel {0} for land co-ord ({1}, {2}) on {3}",
                                //                                new_land.LandData.Name, x, y, m_scene.RegionInfo.RegionName);

                                m_landIDList[x, y] = newLandLocalID;
                            }
                        }
                    }
                }

                m_landList.Add(newLandLocalID, new_land);
                m_landUUIDList[new_land.LandData.GlobalID] = newLandLocalID;
                m_lastLandLocalID++;
            }

            new_land.ForceUpdateLandInfo();
            m_scene.EventManager.TriggerLandObjectAdded(new_land);

            return new_land;
        }

        /// <summary>
        /// Removes a land object from the list. Will not remove if local_id is still owning an area in landIDList
        /// </summary>
        /// <param name="local_id">Land.localID of the peice of land to remove.</param>
        public void removeLandObject(int local_id)
        {
            ILandObject land;
            UUID landGlobalID = UUID.Zero;
            lock (m_landList)
            {
                for (int x = 0; x < m_landIDList.GetLength(0); x++)
                {
                    for (int y = 0; y < m_landIDList.GetLength(1); y++)
                    {
                        if (m_landIDList[x, y] == local_id)
                        {
                            m_log.WarnFormat("[LAND MANAGEMENT MODULE]: Not removing land object {0}; still being used at {1}, {2}",
                                             local_id, x, y);
                            return;
                            //throw new Exception("Could not remove land object. Still being used at " + x + ", " + y);
                        }
                    }
                }

                land = m_landList[local_id];
                m_landList.Remove(local_id);
                if(land != null && land.LandData != null)
                {
                    landGlobalID = land.LandData.GlobalID;
                    m_landUUIDList.Remove(landGlobalID);
                }
            }

            if(landGlobalID != UUID.Zero)
            {
                m_scene.EventManager.TriggerLandObjectRemoved(landGlobalID);
                land.Clear();
            }
        }

        /// <summary>
        /// Clear the scene of all parcels
        /// </summary>
        public void Clear(bool setupDefaultParcel)
        {
            Dictionary<int, ILandObject> landworkList;
            // move to work pointer since we are deleting it all
            lock (m_landList)
            {
                landworkList = m_landList;
                m_landList = new Dictionary<int, ILandObject>();
            }

            // this 2 methods have locks (now)
            ResetSimLandObjects();

            if (setupDefaultParcel)
                CreateDefaultParcel();

            // fire outside events unlocked
            foreach (ILandObject lo in landworkList.Values)
            {
                //m_scene.SimulationDataService.RemoveLandObject(lo.LandData.GlobalID);
                m_scene.EventManager.TriggerLandObjectRemoved(lo.LandData.GlobalID);
            }
            landworkList.Clear();

        }

        private void performFinalLandJoin(ILandObject master, ILandObject slave)
        {
            bool[,] landBitmapSlave = slave.GetLandBitmap();
            lock (m_landList)
            {
                for (int x = 0; x < landBitmapSlave.GetLength(0); x++)
                {
                    for (int y = 0; y < landBitmapSlave.GetLength(1); y++)
                    {
                        if (landBitmapSlave[x, y])
                        {
                            m_landIDList[x, y] = master.LandData.LocalID;
                        }
                    }
                }
            }
            master.LandData.Dwell += slave.LandData.Dwell;
            removeLandObject(slave.LandData.LocalID);
            UpdateLandObject(master.LandData.LocalID, master.LandData);
        }

        public ILandObject GetLandObject(UUID globalID)
        {
            lock (m_landList)
            {
                int lid = -1;
                if(m_landUUIDList.TryGetValue(globalID, out lid) && lid >= 0)
                {
                    if (m_landList.ContainsKey(lid))
                    {
                        return m_landList[lid];
                    }
                    else
                        m_landUUIDList.Remove(globalID); // auto heal
                }
            }
            return null;
        }

        public ILandObject GetLandObject(int parcelLocalID)
        {
            lock (m_landList)
            {
                if (m_landList.ContainsKey(parcelLocalID))
                {
                    return m_landList[parcelLocalID];
                }
            }
            return null;
        }

        /// <summary>
        /// Get the land object at the specified point
        /// </summary>
        /// <param name="x_float">Value between 0 - 256 on the x axis of the point</param>
        /// <param name="y_float">Value between 0 - 256 on the y axis of the point</param>
        /// <returns>Land object at the point supplied</returns>
        public ILandObject GetLandObject(float x_float, float y_float)
        {
            return GetLandObject((int)x_float, (int)y_float, true);
        }

        // if x,y is off region this will return the parcel at cliped x,y
        // as did code it replaces
        public ILandObject GetLandObjectClipedXY(float x, float y)
        {
            //do clip inline
            int avx = (int)x;
            if (avx < 0)
                avx = 0;
            else if (avx >= m_scene.RegionInfo.RegionSizeX)
                avx = (int)Constants.RegionSize - 1;

            int avy = (int)y;
            if (avy < 0)
                avy = 0;
            else if (avy >= m_scene.RegionInfo.RegionSizeY)
                avy = (int)Constants.RegionSize - 1;

            lock (m_landIDList)
            {
                try
                {
                    return m_landList[m_landIDList[avx / LandUnit, avy / LandUnit]];
                }
                catch (IndexOutOfRangeException)
                {
                    return null;
                }
            }
        }

        // Public entry.
        // Throws exception if land object is not found
        public ILandObject GetLandObject(int x, int y)
        {
            return GetLandObject(x, y, false /* returnNullIfLandObjectNotFound */);
        }

        public ILandObject GetLandObject(int x, int y, bool returnNullIfLandObjectOutsideBounds)
        {
            if (x >= m_scene.RegionInfo.RegionSizeX || y >= m_scene.RegionInfo.RegionSizeY || x < 0 || y < 0)
            {
                // These exceptions here will cause a lot of complaints from the users specifically because
                // they happen every time at border crossings
                if (returnNullIfLandObjectOutsideBounds)
                    return null;
                else
                    throw new Exception("Error: Parcel not found at point " + x + ", " + y);
            }

            if(m_landList.Count == 0  || m_landIDList == null)
                return null;

            lock (m_landIDList)
            {
                try
                {
                        return m_landList[m_landIDList[x / LandUnit, y / LandUnit]];
                }
                catch (IndexOutOfRangeException)
                {
                        return null;
                }
            }
        }

        public ILandObject GetLandObjectinLandUnits(int x, int y)
        {
            if (m_landList.Count == 0 || m_landIDList == null)
                return null;

            lock (m_landIDList)
            {
                try
                {
                    return m_landList[m_landIDList[x, y]];
                }
                catch (IndexOutOfRangeException)
                {
                    return null;
                }
            }
        }

        public int GetLandObjectIDinLandUnits(int x, int y)
        {
            lock (m_landIDList)
            {
                try
                {
                    return m_landIDList[x, y];
                }
                catch (IndexOutOfRangeException)
                {
                    return -1;
                }
            }
        }

        // Create a 'parcel is here' bitmap for the parcel identified by the passed landID
        private bool[,] CreateBitmapForID(int landID)
        {
            bool[,] ret = new bool[m_landIDList.GetLength(0), m_landIDList.GetLength(1)];

            for (int xx = 0; xx < m_landIDList.GetLength(0); xx++)
                for (int yy = 0; yy < m_landIDList.GetLength(0); yy++)
                    if (m_landIDList[xx, yy] == landID)
                        ret[xx, yy] = true;

            return ret;
        }

        #endregion

        #region Parcel Modification

        public void ResetOverMeRecords()
        {
            lock (m_landList)
            {
                foreach (LandObject p in m_landList.Values)
                {
                    p.ResetOverMeRecord();
                }
            }
        }

        public void EventManagerOnParcelPrimCountAdd(SceneObjectGroup obj)
        {
            Vector3 position = obj.AbsolutePosition;
            ILandObject landUnderPrim = GetLandObject(position.X, position.Y);
            if (landUnderPrim != null)
            {
                ((LandObject)landUnderPrim).AddPrimOverMe(obj);
            }
        }

        public void EventManagerOnObjectBeingRemovedFromScene(SceneObjectGroup obj)
        {
            lock (m_landList)
            {
                foreach (LandObject p in m_landList.Values)
                {
                    p.RemovePrimFromOverMe(obj);
                }
            }
        }

        private void FinalizeLandPrimCountUpdate()
        {
            //Get Simwide prim count for owner
            Dictionary<UUID, List<LandObject>> landOwnersAndParcels = new Dictionary<UUID, List<LandObject>>();
            lock (m_landList)
            {
                foreach (LandObject p in m_landList.Values)
                {
                    if (!landOwnersAndParcels.ContainsKey(p.LandData.OwnerID))
                    {
                        List<LandObject> tempList = new List<LandObject>();
                        tempList.Add(p);
                        landOwnersAndParcels.Add(p.LandData.OwnerID, tempList);
                    }
                    else
                    {
                        landOwnersAndParcels[p.LandData.OwnerID].Add(p);
                    }
                }
            }

            foreach (UUID owner in landOwnersAndParcels.Keys)
            {
                int simArea = 0;
                int simPrims = 0;
                foreach (LandObject p in landOwnersAndParcels[owner])
                {
                    simArea += p.LandData.Area;
                    simPrims += p.PrimCounts.Total;
                }

                foreach (LandObject p in landOwnersAndParcels[owner])
                {
                    p.LandData.SimwideArea = simArea;
                    p.LandData.SimwidePrims = simPrims;
                }
            }
        }

        public void EventManagerOnParcelPrimCountUpdate()
        {
            //m_log.DebugFormat(
            //    "[land management module]: triggered eventmanageronparcelprimcountupdate() for {0}",
            //    m_scene.RegionInfo.RegionName);

            ResetOverMeRecords();
            EntityBase[] entities = m_scene.Entities.GetEntities();
            foreach (EntityBase obj in entities)
            {
                if (obj != null)
                {
                    if ((obj is SceneObjectGroup) && !obj.IsDeleted && !((SceneObjectGroup) obj).IsAttachment)
                    {
                        m_scene.EventManager.TriggerParcelPrimCountAdd((SceneObjectGroup) obj);
                    }
                }
            }
            FinalizeLandPrimCountUpdate();
        }

        public void EventManagerOnRequestParcelPrimCountUpdate()
        {
            ResetOverMeRecords();
            m_scene.EventManager.TriggerParcelPrimCountUpdate();
            FinalizeLandPrimCountUpdate();
        }

        /// <summary>
        /// Subdivides a piece of land
        /// </summary>
        /// <param name="start_x">West Point</param>
        /// <param name="start_y">South Point</param>
        /// <param name="end_x">East Point</param>
        /// <param name="end_y">North Point</param>
        /// <param name="attempting_user_id">UUID of user who is trying to subdivide</param>
        /// <returns>Returns true if successful</returns>
        public void Subdivide(int start_x, int start_y, int end_x, int end_y, UUID attempting_user_id)
        {
            //First, lets loop through the points and make sure they are all in the same peice of land
            //Get the land object at start

            ILandObject startLandObject = GetLandObject(start_x, start_y);

            if (startLandObject == null)
                return;

            if (!m_scene.Permissions.CanEditParcelProperties(attempting_user_id, startLandObject, GroupPowers.LandDivideJoin, true))
            {
                return;
            }

            //Loop through the points
            try
            {
                for (int y = start_y; y < end_y; y++)
                {
                    for (int x = start_x; x < end_x; x++)
                    {
                        ILandObject tempLandObject = GetLandObject(x, y);
                        if (tempLandObject == null)
                            return;
                        if (tempLandObject != startLandObject)
                            return;
                    }
                }
            }
            catch (Exception)
            {
                return;
            }

             //Lets create a new land object with bitmap activated at that point (keeping the old land objects info)
            ILandObject newLand = startLandObject.Copy();

            newLand.LandData.Name = newLand.LandData.Name;
            newLand.LandData.GlobalID = UUID.Random();
            newLand.LandData.Dwell = 0;
            // Clear "Show in search" on the cut out parcel to prevent double-charging
            newLand.LandData.Flags &= ~(uint)ParcelFlags.ShowDirectory;
            // invalidate landing point
            newLand.LandData.LandingType = (byte)LandingType.Direct;
            newLand.LandData.UserLocation = Vector3.Zero;
            newLand.LandData.UserLookAt = Vector3.Zero;

            newLand.SetLandBitmap(newLand.GetSquareLandBitmap(start_x, start_y, end_x, end_y));

            //lets set the subdivision area of the original to false
            int startLandObjectIndex = startLandObject.LandData.LocalID;
            lock (m_landList)
            {
                m_landList[startLandObjectIndex].SetLandBitmap(
                    newLand.ModifyLandBitmapSquare(startLandObject.GetLandBitmap(), start_x, start_y, end_x, end_y, false));
                m_landList[startLandObjectIndex].ForceUpdateLandInfo();
            }

            //add the new land object
            ILandObject result = AddLandObject(newLand);

            UpdateLandObject(startLandObject.LandData.LocalID, startLandObject.LandData);

            if(startLandObject.LandData.LandingType == (byte)LandingType.LandingPoint)
            {
                int x = (int)startLandObject.LandData.UserLocation.X;
                int y = (int)startLandObject.LandData.UserLocation.Y;
                if(!startLandObject.ContainsPoint(x, y))
                {
                    startLandObject.LandData.LandingType = (byte)LandingType.Direct;
                    startLandObject.LandData.UserLocation = Vector3.Zero;
                    startLandObject.LandData.UserLookAt = Vector3.Zero;
                }
             }

            m_scene.EventManager.TriggerParcelPrimCountTainted();

            result.SendLandUpdateToAvatarsOverMe();
            startLandObject.SendLandUpdateToAvatarsOverMe();
            m_scene.ForEachClient(SendParcelOverlay);

        }

        /// <summary>
        /// Join 2 land objects together
        /// </summary>
        /// <param name="start_x">start x of selection area</param>
        /// <param name="start_y">start y of selection area</param>
        /// <param name="end_x">end x of selection area</param>
        /// <param name="end_y">end y of selection area</param>
        /// <param name="attempting_user_id">UUID of the avatar trying to join the land objects</param>
        /// <returns>Returns true if successful</returns>
        public void Join(int start_x, int start_y, int end_x, int end_y, UUID attempting_user_id)
        {
            int index = 0;
            int maxindex = -1;
            int maxArea = 0;

            List<ILandObject> selectedLandObjects = new List<ILandObject>();
            for (int x = start_x; x < end_x; x += 4)
            {
                for (int y = start_y; y < end_y; y += 4)
                {
                    ILandObject p = GetLandObject(x, y);

                    if (p != null)
                    {
                        if (!selectedLandObjects.Contains(p))
                        {
                            selectedLandObjects.Add(p);
                            if(p.LandData.Area > maxArea)
                            {
                                maxArea = p.LandData.Area;
                                maxindex = index;
                            }
                            index++;
                        }
                    }
                }
            }

            if(maxindex < 0 || selectedLandObjects.Count < 2)
                return;

            ILandObject masterLandObject = selectedLandObjects[maxindex];
            selectedLandObjects.RemoveAt(maxindex);

            if (!m_scene.Permissions.CanEditParcelProperties(attempting_user_id, masterLandObject, GroupPowers.LandDivideJoin, true))
            {
                return;
            }

            UUID masterOwner = masterLandObject.LandData.OwnerID;
            foreach (ILandObject p in selectedLandObjects)
            {
                if (p.LandData.OwnerID != masterOwner)
                    return;
            }

            lock (m_landList)
            {
                foreach (ILandObject slaveLandObject in selectedLandObjects)
                {
                    m_landList[masterLandObject.LandData.LocalID].SetLandBitmap(
                        slaveLandObject.MergeLandBitmaps(masterLandObject.GetLandBitmap(), slaveLandObject.GetLandBitmap()));
                    performFinalLandJoin(masterLandObject, slaveLandObject);
                }
            }

            m_scene.EventManager.TriggerParcelPrimCountTainted();
            masterLandObject.SendLandUpdateToAvatarsOverMe();
            m_scene.ForEachClient(SendParcelOverlay);
        }
        #endregion

        #region Parcel Updating

        /// <summary>
        /// Send the parcel overlay blocks to the client. 
        /// </summary>
        /// <param name="remote_client">The object representing the client</param>
        public void SendParcelOverlay(IClientAPI remote_client)
        {
            if (remote_client.SceneAgent.PresenceType == PresenceType.Npc)
                return;

            const int LAND_BLOCKS_PER_PACKET = 1024;

            int curID;
            int southID;

            byte[] byteArray = new byte[LAND_BLOCKS_PER_PACKET];
            int byteArrayCount = 0;
            int sequenceID = 0;

            int sx = (int)m_scene.RegionInfo.RegionSizeX / LandUnit;
            byte curByte;
            byte tmpByte;

            // Layer data is in LandUnit (4m) chunks
            for (int y = 0; y < m_scene.RegionInfo.RegionSizeY / LandUnit; ++y)
            {
                for (int x = 0; x < sx;)
                {
                    curID = GetLandObjectIDinLandUnits(x,y);
                    if(curID < 0)
                        continue;

                    ILandObject currentParcel = GetLandObject(curID);
                    if (currentParcel == null)
                        continue;

                    // types
                    if (currentParcel.LandData.OwnerID == remote_client.AgentId)
                    {
                        //Owner Flag
                        curByte = LandChannel.LAND_TYPE_OWNED_BY_REQUESTER;
                    }
                    else if (currentParcel.LandData.IsGroupOwned && remote_client.IsGroupMember(currentParcel.LandData.GroupID))
                    {
                        curByte = LandChannel.LAND_TYPE_OWNED_BY_GROUP;
                    }
                    else if (currentParcel.LandData.SalePrice > 0 &&
                                (currentParcel.LandData.AuthBuyerID == UUID.Zero ||
                                currentParcel.LandData.AuthBuyerID == remote_client.AgentId))
                    {
                        //Sale type
                        curByte = LandChannel.LAND_TYPE_IS_FOR_SALE;
                    }
                    else if (currentParcel.LandData.OwnerID == UUID.Zero)
                    {
                        //Public type
                        curByte = LandChannel.LAND_TYPE_PUBLIC; // this does nothing, its zero
                    }
                    // LAND_TYPE_IS_BEING_AUCTIONED still unsuported
                    else
                    {
                        //Other 
                        curByte = LandChannel.LAND_TYPE_OWNED_BY_OTHER;
                    }

                    // now flags
                    // local sound
                    if ((currentParcel.LandData.Flags & (uint)ParcelFlags.SoundLocal) != 0)
                        curByte |= (byte)LandChannel.LAND_FLAG_LOCALSOUND;

                    // hide avatars
                    if (!currentParcel.LandData.SeeAVs)
                        curByte |= (byte)LandChannel.LAND_FLAG_HIDEAVATARS;

                    // border flags for current
                    if (y == 0)
                    {
                        curByte |= LandChannel.LAND_FLAG_PROPERTY_BORDER_SOUTH;
                        tmpByte = curByte;
                    }
                    else
                    {
                        tmpByte = curByte;
                        southID = GetLandObjectIDinLandUnits(x, (y - 1));
                        if (southID >= 0 && southID != curID)
                            tmpByte |= LandChannel.LAND_FLAG_PROPERTY_BORDER_SOUTH;
                    }

                    tmpByte |= LandChannel.LAND_FLAG_PROPERTY_BORDER_WEST;
                    byteArray[byteArrayCount] = tmpByte;
                    byteArrayCount++;

                    if (byteArrayCount >= LAND_BLOCKS_PER_PACKET)
                    {
                        remote_client.SendLandParcelOverlay(byteArray, sequenceID);
                        byteArrayCount = 0;
                        sequenceID++;
                        byteArray = new byte[LAND_BLOCKS_PER_PACKET];
                    }
                    // keep adding while on same parcel, checking south border
                    if (y == 0)
                    {
                        // all have south border and that is already on curByte
                        while (++x < sx && GetLandObjectIDinLandUnits(x, y) == curID)
                        {
                            byteArray[byteArrayCount] = curByte;
                            byteArrayCount++;
                            if (byteArrayCount >= LAND_BLOCKS_PER_PACKET)
                            {
                                remote_client.SendLandParcelOverlay(byteArray, sequenceID);
                                byteArrayCount = 0;
                                sequenceID++;
                                byteArray = new byte[LAND_BLOCKS_PER_PACKET];
                            }
                        }
                    }
                    else
                    {
                        while (++x < sx && GetLandObjectIDinLandUnits(x, y) == curID)
                        {
                            // need to check south one by one
                            southID = GetLandObjectIDinLandUnits(x, (y - 1));
                            if (southID >= 0 && southID != curID)
                            {
                                tmpByte = curByte;
                                tmpByte |= LandChannel.LAND_FLAG_PROPERTY_BORDER_SOUTH;
                                byteArray[byteArrayCount] = tmpByte;
                            }
                            else
                                byteArray[byteArrayCount] = curByte;

                            byteArrayCount++;
                            if (byteArrayCount >= LAND_BLOCKS_PER_PACKET)
                            {
                                remote_client.SendLandParcelOverlay(byteArray, sequenceID);
                                byteArrayCount = 0;
                                sequenceID++;
                                byteArray = new byte[LAND_BLOCKS_PER_PACKET];
                            }
                        }
                    }
                }
            }

            if (byteArrayCount > 0)
            {
                remote_client.SendLandParcelOverlay(byteArray, sequenceID);
            }
        }

        public void ClientOnParcelPropertiesRequest(int start_x, int start_y, int end_x, int end_y, int sequence_id,
                                                    bool snap_selection, IClientAPI remote_client)
        {
            //Get the land objects within the bounds
            List<ILandObject> temp = new List<ILandObject>();
            int inc_x = end_x - start_x;
            int inc_y = end_y - start_y;
            for (int x = 0; x < inc_x; x++)
            {
                for (int y = 0; y < inc_y; y++)
                {
                    ILandObject currentParcel = GetLandObject(start_x + x, start_y + y);

                    if (currentParcel != null)
                    {
                        if (!temp.Contains(currentParcel))
                        {
                            if (!currentParcel.IsBannedFromLand(remote_client.AgentId))
                            {
                                currentParcel.ForceUpdateLandInfo();
                                temp.Add(currentParcel);
                            }
                        }
                    }
                }
            }

            int requestResult = LandChannel.LAND_RESULT_SINGLE;
            if (temp.Count > 1)
            {
                requestResult = LandChannel.LAND_RESULT_MULTIPLE;
            }

            for (int i = 0; i < temp.Count; i++)
            {
                temp[i].SendLandProperties(sequence_id, snap_selection, requestResult, remote_client);
            }

//            SendParcelOverlay(remote_client);
        }

        public void UpdateLandProperties(ILandObject land, LandUpdateArgs args, IClientAPI remote_client)
        {
            bool snap_selection = false;
            bool needOverlay = false;
            if (land.UpdateLandProperties(args, remote_client, out snap_selection, out needOverlay))
            {
                UUID parcelID = land.LandData.GlobalID;
                m_scene.ForEachScenePresence(delegate(ScenePresence avatar)
                {
                    if (avatar.IsDeleted || avatar.IsNPC)
                        return;

                    IClientAPI client = avatar.ControllingClient;
                    if (needOverlay)
                        SendParcelOverlay(client);

                    if (avatar.IsChildAgent)
                    {
                        if(client == remote_client)
                            land.SendLandProperties(-10000, false, LandChannel.LAND_RESULT_SINGLE, client);
                        return;
                    }

                    ILandObject aland = GetLandObject(avatar.AbsolutePosition.X, avatar.AbsolutePosition.Y);
                    if (aland != null)
                    {
                        if(client == remote_client && land != aland)
                            land.SendLandProperties(-10000, false, LandChannel.LAND_RESULT_SINGLE, client);
                        else if (land == aland)
                             aland.SendLandProperties(0, false, LandChannel.LAND_RESULT_SINGLE, client);
                    }
                    if (avatar.currentParcelUUID == parcelID)
                        avatar.currentParcelUUID = parcelID; // force parcel flags review
                });
            }
        }

        public void ClientOnParcelPropertiesUpdateRequest(LandUpdateArgs args, int localID, IClientAPI remote_client)
        {
            ILandObject land;
            lock (m_landList)
            {
                m_landList.TryGetValue(localID, out land);
            }

            if (land != null)
            {
                UpdateLandProperties(land, args, remote_client);
                m_scene.EventManager.TriggerOnParcelPropertiesUpdateRequest(args, localID, remote_client);
            }
        }

        public void ClientOnParcelDivideRequest(int west, int south, int east, int north, IClientAPI remote_client)
        {
            Subdivide(west, south, east, north, remote_client.AgentId);
        }

        public void ClientOnParcelJoinRequest(int west, int south, int east, int north, IClientAPI remote_client)
        {
            Join(west, south, east, north, remote_client.AgentId);
        }

        public void ClientOnParcelSelectObjects(int local_id, int request_type,
                                                List<UUID> returnIDs, IClientAPI remote_client)
        {
            m_landList[local_id].SendForceObjectSelect(local_id, request_type, returnIDs, remote_client);
        }

        public void ClientOnParcelObjectOwnerRequest(int local_id, IClientAPI remote_client)
        {
            ILandObject land = null;
            lock (m_landList)
            {
                m_landList.TryGetValue(local_id, out land);
            }

            if (land != null)
            {
                m_scene.EventManager.TriggerParcelPrimCountUpdate();
                land.SendLandObjectOwners(remote_client);
            }
            else
            {
                m_log.WarnFormat("[LAND MANAGEMENT MODULE]: Invalid land object {0} passed for parcel object owner request", local_id);
            }
        }

        public void ClientOnParcelGodForceOwner(int local_id, UUID ownerID, IClientAPI remote_client)
        {
            ILandObject land = null;
            lock (m_landList)
            {
                m_landList.TryGetValue(local_id, out land);
            }

            if (land != null)
            {
                if (m_scene.Permissions.IsGod(remote_client.AgentId))
                {
                    land.LandData.OwnerID = ownerID;
                    land.LandData.GroupID = UUID.Zero;
                    land.LandData.IsGroupOwned = false;
                    land.LandData.Flags &= ~(uint) (ParcelFlags.ForSale | ParcelFlags.ForSaleObjects | ParcelFlags.SellParcelObjects | ParcelFlags.ShowDirectory);
                    m_scene.ForEachClient(SendParcelOverlay);
                    land.SendLandUpdateToClient(true, remote_client);
                    UpdateLandObject(land.LandData.LocalID, land.LandData);
                }
            }
        }

        public void ClientOnParcelAbandonRequest(int local_id, IClientAPI remote_client)
        {
            ILandObject land = null;
            lock (m_landList)
            {
                m_landList.TryGetValue(local_id, out land);
            }

            if (land != null)
            {
                if (m_scene.Permissions.CanAbandonParcel(remote_client.AgentId, land))
                {
                    land.LandData.OwnerID = m_scene.RegionInfo.EstateSettings.EstateOwner;
                    land.LandData.GroupID = UUID.Zero;
                    land.LandData.IsGroupOwned = false;
                    land.LandData.Flags &= ~(uint) (ParcelFlags.ForSale | ParcelFlags.ForSaleObjects | ParcelFlags.SellParcelObjects | ParcelFlags.ShowDirectory);

                    m_scene.ForEachClient(SendParcelOverlay);
                    land.SendLandUpdateToClient(true, remote_client);
                    UpdateLandObject(land.LandData.LocalID, land.LandData);
                }
            }
        }

        public void ClientOnParcelReclaim(int local_id, IClientAPI remote_client)
        {
            ILandObject land = null;
            lock (m_landList)
            {
                m_landList.TryGetValue(local_id, out land);
            }

            if (land != null)
            {
                if (m_scene.Permissions.CanReclaimParcel(remote_client.AgentId, land))
                {
                    land.LandData.OwnerID = m_scene.RegionInfo.EstateSettings.EstateOwner;
                    land.LandData.ClaimDate = Util.UnixTimeSinceEpoch();
                    land.LandData.GroupID = UUID.Zero;
                    land.LandData.IsGroupOwned = false;
                    land.LandData.SalePrice = 0;
                    land.LandData.AuthBuyerID = UUID.Zero;
                    land.LandData.SeeAVs = true;
                    land.LandData.AnyAVSounds = true;
                    land.LandData.GroupAVSounds = true;
                    land.LandData.Flags &= ~(uint) (ParcelFlags.ForSale | ParcelFlags.ForSaleObjects | ParcelFlags.SellParcelObjects | ParcelFlags.ShowDirectory);
                    m_scene.ForEachClient(SendParcelOverlay);
                    land.SendLandUpdateToClient(true, remote_client);
                    UpdateLandObject(land.LandData.LocalID, land.LandData);
                }
            }
        }
        #endregion

        // If the economy has been validated by the economy module,
        // and land has been validated as well, this method transfers
        // the land ownership

        public void EventManagerOnLandBuy(Object o, EventManager.LandBuyArgs e)
        {
            if (e.economyValidated && e.landValidated)
            {
                ILandObject land;
                lock (m_landList)
                {
                    m_landList.TryGetValue(e.parcelLocalID, out land);
                }

                if (land != null)
                {
                    land.UpdateLandSold(e.agentId, e.groupId, e.groupOwned, (uint)e.transactionID, e.parcelPrice, e.parcelArea);
                }
            }
        }

        // After receiving a land buy packet, first the data needs to
        // be validated. This method validates the right to buy the
        // parcel

        public void EventManagerOnValidateLandBuy(Object o, EventManager.LandBuyArgs e)
        {
            if (e.landValidated == false)
            {
                ILandObject lob = null;
                lock (m_landList)
                {
                    m_landList.TryGetValue(e.parcelLocalID, out lob);
                }

                if (lob != null)
                {
                    UUID AuthorizedID = lob.LandData.AuthBuyerID;
                    int saleprice = lob.LandData.SalePrice;
                    UUID pOwnerID = lob.LandData.OwnerID;

                    bool landforsale = ((lob.LandData.Flags &
                                         (uint)(ParcelFlags.ForSale | ParcelFlags.ForSaleObjects | ParcelFlags.SellParcelObjects)) != 0);
                    if ((AuthorizedID == UUID.Zero || AuthorizedID == e.agentId) && e.parcelPrice >= saleprice && landforsale)
                    {
                        // TODO I don't think we have to lock it here, no?
                        //lock (e)
                        //{
                            e.parcelOwnerID = pOwnerID;
                            e.landValidated = true;
                        //}
                    }
                }
            }
        }

        void ClientOnParcelDeedToGroup(int parcelLocalID, UUID groupID, IClientAPI remote_client)
        {
            ILandObject land = null;
            lock (m_landList)
            {
                m_landList.TryGetValue(parcelLocalID, out land);
            }

            if (land != null)
            {
                if (!m_scene.Permissions.CanDeedParcel(remote_client.AgentId, land))
                    return;
                land.DeedToGroup(groupID);
            }
        }

        #region Land Object From Storage Functions

        private void EventManagerOnIncomingLandDataFromStorage(List<LandData> data)
        {
            lock (m_landList)
            {
                for (int i = 0; i < data.Count; i++)
                    IncomingLandObjectFromStorage(data[i]);

                // Layer data is in LandUnit (4m) chunks
                for (int y = 0; y < m_scene.RegionInfo.RegionSizeY / Constants.TerrainPatchSize * (Constants.TerrainPatchSize / LandUnit); y++)
                {
                    for (int x = 0; x < m_scene.RegionInfo.RegionSizeX / Constants.TerrainPatchSize * (Constants.TerrainPatchSize / LandUnit); x++)
                    {
                        if (m_landIDList[x, y] == 0)
                        {
                            if (m_landList.Count == 1)
                            {
                                m_log.DebugFormat(
                                    "[{0}]: Auto-extending land parcel as landID at {1},{2} is 0 and only one land parcel is present in {3}",
                                    LogHeader, x, y, m_scene.Name);

                                int onlyParcelID = 0;
                                ILandObject onlyLandObject = null;
                                foreach (KeyValuePair<int, ILandObject> kvp in m_landList)
                                {
                                    onlyParcelID = kvp.Key;
                                    onlyLandObject = kvp.Value;
                                    break;
                                }

                                // There is only one parcel. Grow it to fill all the unallocated spaces.
                                for (int xx = 0; xx < m_landIDList.GetLength(0); xx++)
                                    for (int yy = 0; yy < m_landIDList.GetLength(1); yy++)
                                        if (m_landIDList[xx, yy] == 0)
                                            m_landIDList[xx, yy] = onlyParcelID;

                                onlyLandObject.LandBitmap = CreateBitmapForID(onlyParcelID);
                            }
                            else if (m_landList.Count > 1)
                            {
                                m_log.DebugFormat(
                                    "{0}: Auto-creating land parcel as landID at {1},{2} is 0 and more than one land parcel is present in {3}",
                                    LogHeader, x, y, m_scene.Name);

                                // There are several other parcels so we must create a new one for the unassigned space
                                ILandObject newLand = new LandObject(UUID.Zero, false, m_scene);
                                // Claim all the unclaimed "0" ids
                                newLand.SetLandBitmap(CreateBitmapForID(0));
                                newLand.LandData.OwnerID = m_scene.RegionInfo.EstateSettings.EstateOwner;
                                newLand.LandData.ClaimDate = Util.UnixTimeSinceEpoch();
                                newLand = AddLandObject(newLand);
                            }
                            else
                            {
                                // We should never reach this point as the separate code path when no land data exists should have fired instead.
                                m_log.WarnFormat(
                                    "{0}: Ignoring request to auto-create parcel in {1} as there are no other parcels present",
                                    LogHeader, m_scene.Name);
                            }
                        }
                    }
                }
                FinalizeLandPrimCountUpdate(); // update simarea information
            }
        }

        private void IncomingLandObjectFromStorage(LandData data)
        {
            ILandObject new_land = new LandObject(data.OwnerID, data.IsGroupOwned, m_scene, data);

            new_land.SetLandBitmapFromByteArray();
            AddLandObject(new_land);
//            new_land.SendLandUpdateToAvatarsOverMe();
        }

        public void ReturnObjectsInParcel(int localID, uint returnType, UUID[] agentIDs, UUID[] taskIDs, IClientAPI remoteClient)
        {
            if (localID != -1)
            {
                ILandObject selectedParcel = null;
                lock (m_landList)
                {
                    m_landList.TryGetValue(localID, out selectedParcel);
                }

                if (selectedParcel == null)
                    return;

                selectedParcel.ReturnLandObjects(returnType, agentIDs, taskIDs, remoteClient);
            }
            else
            {
                if (returnType != 1)
                {
                    m_log.WarnFormat("[LAND MANAGEMENT MODULE]: ReturnObjectsInParcel: unknown return type {0}", returnType);
                    return;
                }

                // We get here when the user returns objects from the list of Top Colliders or Top Scripts.
                // In that case we receive specific object UUID's, but no parcel ID.

                Dictionary<UUID, HashSet<SceneObjectGroup>> returns = new Dictionary<UUID, HashSet<SceneObjectGroup>>();

                foreach (UUID groupID in taskIDs)
                {
                    SceneObjectGroup obj = m_scene.GetSceneObjectGroup(groupID);
                    if (obj != null)
                    {
                        if (!returns.ContainsKey(obj.OwnerID))
                            returns[obj.OwnerID] = new HashSet<SceneObjectGroup>();
                        returns[obj.OwnerID].Add(obj);
                    }
                    else
                    {
                        m_log.WarnFormat("[LAND MANAGEMENT MODULE]: ReturnObjectsInParcel: unknown object {0}", groupID);
                    }
                }

                int num = 0;
                foreach (HashSet<SceneObjectGroup> objs in returns.Values)
                    num += objs.Count;
                m_log.DebugFormat("[LAND MANAGEMENT MODULE]: Returning {0} specific object(s)", num);

                foreach (HashSet<SceneObjectGroup> objs in returns.Values)
                {
                    List<SceneObjectGroup> objs2 = new List<SceneObjectGroup>(objs);
                    if (m_scene.Permissions.CanReturnObjects(null, remoteClient, objs2))
                    {
                        m_scene.returnObjects(objs2.ToArray(), remoteClient);
                    }
                    else
                    {
                        m_log.WarnFormat("[LAND MANAGEMENT MODULE]: ReturnObjectsInParcel: not permitted to return {0} object(s) belonging to user {1}",
                            objs2.Count, objs2[0].OwnerID);
                    }
                }
            }
        }

        public void EventManagerOnNoLandDataFromStorage()
        {
            ResetSimLandObjects();
            CreateDefaultParcel();
        }

        #endregion

        public void setParcelObjectMaxOverride(overrideParcelMaxPrimCountDelegate overrideDel)
        {
            lock (m_landList)
            {
                foreach (LandObject obj in m_landList.Values)
                {
                    obj.SetParcelObjectMaxOverride(overrideDel);
                }
            }
        }

        public void setSimulatorObjectMaxOverride(overrideSimulatorMaxPrimCountDelegate overrideDel)
        {
        }

        #region CAPS handler

        private void EventManagerOnRegisterCaps(UUID agentID, Caps caps)
        {
            string cap = "/CAPS/" + UUID.Random();
            caps.RegisterHandler(
                "RemoteParcelRequest",
                new RestStreamHandler(
                    "POST", cap,
                    (request, path, param, httpRequest, httpResponse)
                        => RemoteParcelRequest(request, path, param, agentID, caps),
                    "RemoteParcelRequest",
                    agentID.ToString()));

            cap = "/CAPS/" + UUID.Random();
            caps.RegisterHandler(
                "ParcelPropertiesUpdate",
                new RestStreamHandler(
                    "POST", cap,
                    (request, path, param, httpRequest, httpResponse)
                        => ProcessPropertiesUpdate(request, path, param, agentID, caps),
                    "ParcelPropertiesUpdate",
                    agentID.ToString()));
        }
        private string ProcessPropertiesUpdate(string request, string path, string param, UUID agentID, Caps caps)
        {
            IClientAPI client;
            if (!m_scene.TryGetClient(agentID, out client))
            {
                m_log.WarnFormat("[LAND MANAGEMENT MODULE]: Unable to retrieve IClientAPI for {0}", agentID);
                return LLSDHelpers.SerialiseLLSDReply(new LLSDEmpty());
            }

            ParcelPropertiesUpdateMessage properties = new ParcelPropertiesUpdateMessage();
            OpenMetaverse.StructuredData.OSDMap args = (OpenMetaverse.StructuredData.OSDMap) OSDParser.DeserializeLLSDXml(request);

            properties.Deserialize(args);

            LandUpdateArgs land_update = new LandUpdateArgs();
            int parcelID = properties.LocalID;
            land_update.AuthBuyerID = properties.AuthBuyerID;
            land_update.Category = properties.Category;
            land_update.Desc = properties.Desc;
            land_update.GroupID = properties.GroupID;
            land_update.LandingType = (byte) properties.Landing;
            land_update.MediaAutoScale = (byte) Convert.ToInt32(properties.MediaAutoScale);
            land_update.MediaID = properties.MediaID;
            land_update.MediaURL = properties.MediaURL;
            land_update.MusicURL = properties.MusicURL;
            land_update.Name = properties.Name;
            land_update.ParcelFlags = (uint) properties.ParcelFlags;
            land_update.PassHours = properties.PassHours;
            land_update.PassPrice = (int) properties.PassPrice;
            land_update.SalePrice = (int) properties.SalePrice;
            land_update.SnapshotID = properties.SnapshotID;
            land_update.UserLocation = properties.UserLocation;
            land_update.UserLookAt = properties.UserLookAt;
            land_update.MediaDescription = properties.MediaDesc;
            land_update.MediaType = properties.MediaType;
            land_update.MediaWidth = properties.MediaWidth;
            land_update.MediaHeight = properties.MediaHeight;
            land_update.MediaLoop = properties.MediaLoop;
            land_update.ObscureMusic = properties.ObscureMusic;
            land_update.ObscureMedia = properties.ObscureMedia;

            if (args.ContainsKey("see_avs"))
            {
                land_update.SeeAVs = args["see_avs"].AsBoolean();
                land_update.AnyAVSounds = args["any_av_sounds"].AsBoolean();
                land_update.GroupAVSounds = args["group_av_sounds"].AsBoolean();
            }
            else
            {
                land_update.SeeAVs = true;
                land_update.AnyAVSounds = true;
                land_update.GroupAVSounds = true;
            }

            ILandObject land = null;
            lock (m_landList)
            {
                m_landList.TryGetValue(parcelID, out land);
            }

            if (land != null)
            {
                UpdateLandProperties(land,land_update, client);
                m_scene.EventManager.TriggerOnParcelPropertiesUpdateRequest(land_update, parcelID, client);
            }
            else
            {
                m_log.WarnFormat("[LAND MANAGEMENT MODULE]: Unable to find parcelID {0}", parcelID);
            }

            return LLSDxmlEncode.LLSDEmpty;
        }
        // we cheat here: As we don't have (and want) a grid-global parcel-store, we can't return the
        // "real" parcelID, because we wouldn't be able to map that to the region the parcel belongs to.
        // So, we create a "fake" parcelID by using the regionHandle (64 bit), and the local (integer) x
        // and y coordinate (each 8 bit), encoded in a UUID (128 bit).
        //
        // Request format:
        // <llsd>
        //   <map>
        //     <key>location</key>
        //     <array>
        //       <real>1.23</real>
        //       <real>45..6</real>
        //       <real>78.9</real>
        //     </array>
        //     <key>region_id</key>
        //     <uuid>xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx</uuid>
        //   </map>
        // </llsd>
        private string RemoteParcelRequest(string request, string path, string param, UUID agentID, Caps caps)
        {
            UUID parcelID = UUID.Zero;
            try
            {
                Hashtable hash = new Hashtable();
                hash = (Hashtable)LLSD.LLSDDeserialize(Utils.StringToBytes(request));
                if (hash.ContainsKey("location"))
                {
                    UUID scope = m_scene.RegionInfo.ScopeID;
                    ArrayList list = (ArrayList)hash["location"];
                    uint x = (uint)(double)list[0];
                    uint y = (uint)(double)list[1];
                    if (hash.ContainsKey("region_handle"))
                    {
                        // if you do a "About Landmark" on a landmark a second time, the viewer sends the
                        // region_handle it got earlier via RegionHandleRequest
                        ulong regionHandle = Util.BytesToUInt64Big((byte[])hash["region_handle"]);
                        if(regionHandle == m_scene.RegionInfo.RegionHandle)
                            parcelID = Util.BuildFakeParcelID(regionHandle, x, y);
                        else
                        {
                            uint wx;
                            uint wy;
                            Util.RegionHandleToWorldLoc(regionHandle, out wx, out wy);
                            GridRegion info = m_scene.GridService.GetRegionByPosition(scope, (int)wx, (int)wy);
                            if(info != null)
                            {
                                wx -= (uint)info.RegionLocX;
                                wy -= (uint)info.RegionLocY;
                                wx += x;
                                wy += y;
                                // Firestorm devs have no ideia how to do handlers math
                                // on all cases
                                if(wx > info.RegionSizeX || wy > info.RegionSizeY)
                                {
                                    wx = x;
                                    wy = y;
                                }
                                parcelID = Util.BuildFakeParcelID(info.RegionHandle, wx, wy);
                            }
                        }
                    }
                    else if(hash.ContainsKey("region_id"))
                    {
                        UUID regionID = (UUID)hash["region_id"];
                        if (regionID == m_scene.RegionInfo.RegionID)
                        {
                        // a parcel request for a local parcel => no need to query the grid
                            parcelID = Util.BuildFakeParcelID(m_scene.RegionInfo.RegionHandle, x, y);
                        }
                        else
                        {
                            // a parcel request for a parcel in another region. Ask the grid about the region
                            GridRegion info = m_scene.GridService.GetRegionByUUID(scope, regionID);
                            if (info != null)
                                parcelID = Util.BuildFakeParcelID(info.RegionHandle, x, y);
                        }
                    }
                }
            }
            catch (LLSD.LLSDParseException e)
            {
                m_log.ErrorFormat("[LAND MANAGEMENT MODULE]: Fetch error: {0}", e.Message);
                m_log.ErrorFormat("[LAND MANAGEMENT MODULE]: ... in request {0}", request);
            }
            catch (InvalidCastException)
            {
                m_log.ErrorFormat("[LAND MANAGEMENT MODULE]: Wrong type in request {0}", request);
            }

            //m_log.DebugFormat("[LAND MANAGEMENT MODULE]: Got parcelID {0}", parcelID);
            StringBuilder sb = LLSDxmlEncode.Start();
                LLSDxmlEncode.AddMap(sb);
                  LLSDxmlEncode.AddElem("parcel_id", parcelID,sb);
                LLSDxmlEncode.AddEndMap(sb);
            return LLSDxmlEncode.End(sb);
        }

        #endregion

        private void ClientOnParcelInfoRequest(IClientAPI remoteClient, UUID parcelID)
        {
            if (parcelID == UUID.Zero)
                return;

            ExtendedLandData data = (ExtendedLandData)parcelInfoCache.Get(parcelID.ToString(),
                    delegate(string id)
                    {
                        UUID parcel = UUID.Zero;
                        UUID.TryParse(id, out parcel);
                        // assume we've got the parcelID we just computed in RemoteParcelRequest
                        ExtendedLandData extLandData = new ExtendedLandData();
                        if(!Util.ParseFakeParcelID(parcel, out extLandData.RegionHandle,
                                               out extLandData.X, out extLandData.Y))
                            return null;
                        m_log.DebugFormat("[LAND MANAGEMENT MODULE]: Got parcelinfo request for regionHandle {0}, x/y {1}/{2}",
                                          extLandData.RegionHandle, extLandData.X, extLandData.Y);

                        // for this region or for somewhere else?
                        if (extLandData.RegionHandle == m_scene.RegionInfo.RegionHandle)
                        {
                            ILandObject extLandObject = this.GetLandObject(extLandData.X, extLandData.Y);
                            if (extLandObject == null)
                            {
                                m_log.DebugFormat("[LAND MANAGEMENT MODULE]: ParcelInfoRequest: a FakeParcelID points to outside the region");
                                return null;
                            }
                            extLandData.LandData = extLandObject.LandData;
                            extLandData.RegionAccess = m_scene.RegionInfo.AccessLevel;
                        }
                        else
                        {
                            ILandService landService = m_scene.RequestModuleInterface<ILandService>();
                            extLandData.LandData = landService.GetLandData(m_scene.RegionInfo.ScopeID,
                                    extLandData.RegionHandle,
                                    extLandData.X,
                                    extLandData.Y,
                                    out extLandData.RegionAccess);
                            if (extLandData.LandData == null)
                            {
                                // we didn't find the region/land => don't cache
                                return null;
                            }
                        }
                        return extLandData;
                    });

            if (data != null)  // if we found some data, send it
            {
                GridRegion info;
                if (data.RegionHandle == m_scene.RegionInfo.RegionHandle)
                {
                    info = new GridRegion(m_scene.RegionInfo);
                    IDwellModule dwellModule = m_scene.RequestModuleInterface<IDwellModule>();
                    if (dwellModule != null)
                        data.LandData.Dwell = dwellModule.GetDwell(data.LandData);
                }
                else
                {
                    // most likely still cached from building the extLandData entry
                    uint x = 0, y = 0;
                    Util.RegionHandleToWorldLoc(data.RegionHandle, out x, out y);
                    info = m_scene.GridService.GetRegionByPosition(m_scene.RegionInfo.ScopeID, (int)x, (int)y);
                }
                // we need to transfer the fake parcelID, not the one in landData, so the viewer can match it to the landmark.
                m_log.DebugFormat("[LAND MANAGEMENT MODULE]: got parcelinfo for parcel {0} in region {1}; sending...",
                                  data.LandData.Name, data.RegionHandle);
                // HACK for now
                RegionInfo r = new RegionInfo();
                r.RegionName = info.RegionName;
                r.RegionLocX = (uint)info.RegionLocX;
                r.RegionLocY = (uint)info.RegionLocY;
                r.RegionSettings.Maturity = (int)Util.ConvertAccessLevelToMaturity(data.RegionAccess);
                remoteClient.SendParcelInfo(r, data.LandData, parcelID, data.X, data.Y);
            }
            else
                m_log.Debug("[LAND MANAGEMENT MODULE]: got no parcelinfo; not sending");
        }

        public void setParcelOtherCleanTime(IClientAPI remoteClient, int localID, int otherCleanTime)
        {
            ILandObject land = null;
            lock (m_landList)
            {
                m_landList.TryGetValue(localID, out land);
            }

            if (land == null) return;

            if (!m_scene.Permissions.CanEditParcelProperties(remoteClient.AgentId, land, GroupPowers.LandOptions, false))
                return;

            land.LandData.OtherCleanTime = otherCleanTime;

            UpdateLandObject(localID, land.LandData);
        }

        public void ClientOnParcelGodMark(IClientAPI client, UUID god, int landID)
        {
            ScenePresence sp = null;
            ((Scene)client.Scene).TryGetScenePresence(client.AgentId, out sp);
            if (sp == null)
                return;
            if (sp.IsChildAgent || sp.IsDeleted || sp.IsInTransit || sp.IsNPC)
                return;
            if (!sp.IsGod)
            {
                client.SendAlertMessage("Request denied. You're not priviliged.");
                return;
            }

            ILandObject land = null;
            List<ILandObject> Lands = ((Scene)client.Scene).LandChannel.AllParcels();
            foreach (ILandObject landObject in Lands)
            {
                if (landObject.LandData.LocalID == landID)
                { 
                    land = landObject;
                    break;
                }
            }
            if (land == null)
                return;

            bool validParcelOwner = false;
            if (DefaultGodParcelOwner != UUID.Zero && m_scene.UserAccountService.GetUserAccount(m_scene.RegionInfo.ScopeID, DefaultGodParcelOwner) != null)
                validParcelOwner = true;

            bool validParcelGroup = false;
            if (m_groupManager != null)
            {
                if (DefaultGodParcelGroup != UUID.Zero && m_groupManager.GetGroupRecord(DefaultGodParcelGroup) != null)
                    validParcelGroup = true;
            }

            if (!validParcelOwner && !validParcelGroup)
            {
                client.SendAlertMessage("Please check ini files.\n[LandManagement] config section.");
                return;
            }

            land.LandData.AnyAVSounds = true;
            land.LandData.SeeAVs = true;
            land.LandData.GroupAVSounds = true;
            land.LandData.AuthBuyerID = UUID.Zero;
            land.LandData.Category = ParcelCategory.None;
            land.LandData.ClaimDate = Util.UnixTimeSinceEpoch();
            land.LandData.Description = String.Empty;
            land.LandData.Dwell = 0;
            land.LandData.Flags = (uint)ParcelFlags.AllowFly | (uint)ParcelFlags.AllowLandmark |
                                (uint)ParcelFlags.AllowAPrimitiveEntry |
                                (uint)ParcelFlags.AllowDeedToGroup |
                                (uint)ParcelFlags.CreateObjects | (uint)ParcelFlags.AllowOtherScripts |
                                (uint)ParcelFlags.AllowVoiceChat;
            land.LandData.LandingType = (byte)LandingType.Direct;
            land.LandData.LastDwellTimeMS = Util.GetTimeStampMS();
            land.LandData.MediaAutoScale = 0;
            land.LandData.MediaDescription = "";
            land.LandData.MediaHeight = 0;
            land.LandData.MediaID = UUID.Zero;
            land.LandData.MediaLoop = false;
            land.LandData.MediaType = "none/none";
            land.LandData.MediaURL = String.Empty;
            land.LandData.MediaWidth = 0;
            land.LandData.MusicURL = String.Empty;
            land.LandData.ObscureMedia = false;
            land.LandData.ObscureMusic = false;
            land.LandData.OtherCleanTime = 0;
            land.LandData.ParcelAccessList = new List<LandAccessEntry>();
            land.LandData.PassHours = 0;
            land.LandData.PassPrice = 0;
            land.LandData.SalePrice = 0;
            land.LandData.SnapshotID = UUID.Zero;
            land.LandData.Status = ParcelStatus.Leased;

            if (validParcelOwner)
            {
                land.LandData.OwnerID = DefaultGodParcelOwner;
                land.LandData.IsGroupOwned = false;
            }
            else
            {
                land.LandData.OwnerID = DefaultGodParcelGroup;
                land.LandData.IsGroupOwned = true;
            }

            if (validParcelGroup)
                land.LandData.GroupID = DefaultGodParcelGroup;
            else
                land.LandData.GroupID = UUID.Zero;

            land.LandData.Name = DefaultGodParcelName;
            m_scene.ForEachClient(SendParcelOverlay);
            land.SendLandUpdateToClient(true, client);
            UpdateLandObject(land.LandData.LocalID, land.LandData);
            m_scene.EventManager.TriggerParcelPrimCountUpdate();
        }

        private void ClientOnSimWideDeletes(IClientAPI client, UUID agentID, int flags, UUID targetID)
        {
            ScenePresence SP;
            ((Scene)client.Scene).TryGetScenePresence(client.AgentId, out SP);
            List<SceneObjectGroup> returns = new List<SceneObjectGroup>();
            if (SP.GodController.UserLevel != 0)
            {
                if (flags == 0) //All parcels, scripted or not
                {
                    ((Scene)client.Scene).ForEachSOG(delegate(SceneObjectGroup e)
                    {
                        if (e.OwnerID == targetID)
                        {
                            returns.Add(e);
                        }
                    }
                                                    );
                }
                if (flags == 4) //All parcels, scripted object
                {
                    ((Scene)client.Scene).ForEachSOG(delegate(SceneObjectGroup e)
                    {
                        if (e.OwnerID == targetID)
                        {
                            if (e.ContainsScripts())
                            {
                                returns.Add(e);
                            }
                        }
                    });
                }
                if (flags == 4) //not target parcel, scripted object
                {
                    ((Scene)client.Scene).ForEachSOG(delegate(SceneObjectGroup e)
                    {
                        if (e.OwnerID == targetID)
                        {
                            ILandObject landobject = ((Scene)client.Scene).LandChannel.GetLandObject(e.AbsolutePosition.X, e.AbsolutePosition.Y);
                            if (landobject.LandData.OwnerID != e.OwnerID)
                            {
                                if (e.ContainsScripts())
                                {
                                    returns.Add(e);
                                }
                            }
                        }
                    });
                }
                foreach (SceneObjectGroup ol in returns)
                {
                    ReturnObject(ol, client);
                }
            }
        }
        public void ReturnObject(SceneObjectGroup obj, IClientAPI client)
        {
            SceneObjectGroup[] objs = new SceneObjectGroup[1];
            objs[0] = obj;
            ((Scene)client.Scene).returnObjects(objs, client);
        }

        Dictionary<UUID, System.Threading.Timer> Timers = new Dictionary<UUID, System.Threading.Timer>();

        public void ClientOnParcelFreezeUser(IClientAPI client, UUID parcelowner, uint flags, UUID target)
        {
            ScenePresence targetAvatar = null;
            ((Scene)client.Scene).TryGetScenePresence(target, out targetAvatar);
            ScenePresence parcelManager = null;
            ((Scene)client.Scene).TryGetScenePresence(client.AgentId, out parcelManager);
            System.Threading.Timer Timer;

            if (targetAvatar.GodController.UserLevel < 200)
            {
                ILandObject land = ((Scene)client.Scene).LandChannel.GetLandObject(targetAvatar.AbsolutePosition.X, targetAvatar.AbsolutePosition.Y);
                if (!((Scene)client.Scene).Permissions.CanEditParcelProperties(client.AgentId, land, GroupPowers.LandEjectAndFreeze, true))
                    return;
                if ((flags & 1) == 0) // only lowest bit has meaning for now
                {
                    targetAvatar.AllowMovement = false;
                    targetAvatar.ControllingClient.SendAlertMessage(parcelManager.Firstname + " " + parcelManager.Lastname + " has frozen you for 30 seconds.  You cannot move or interact with the world.");
                    parcelManager.ControllingClient.SendAlertMessage("Avatar Frozen.");
                    System.Threading.TimerCallback timeCB = new System.Threading.TimerCallback(OnEndParcelFrozen);
                    Timer = new System.Threading.Timer(timeCB, targetAvatar, 30000, 0);
                    Timers.Add(targetAvatar.UUID, Timer);
                }
                else
                {
                    targetAvatar.AllowMovement = true;
                    targetAvatar.ControllingClient.SendAlertMessage(parcelManager.Firstname + " " + parcelManager.Lastname + " has unfrozen you.");
                    parcelManager.ControllingClient.SendAlertMessage("Avatar Unfrozen.");
                    Timers.TryGetValue(targetAvatar.UUID, out Timer);
                    Timers.Remove(targetAvatar.UUID);
                    Timer.Dispose();
                }
            }
        }
        private void OnEndParcelFrozen(object avatar)
        {
            ScenePresence targetAvatar = (ScenePresence)avatar;
            targetAvatar.AllowMovement = true;
            System.Threading.Timer Timer;
            Timers.TryGetValue(targetAvatar.UUID, out Timer);
            Timers.Remove(targetAvatar.UUID);
            targetAvatar.ControllingClient.SendAgentAlertMessage("The freeze has worn off; you may go about your business.", false);
        }

        public void ClientOnParcelEjectUser(IClientAPI client, UUID parcelowner, uint flags, UUID target)
        {
            ScenePresence targetAvatar = null;
            ScenePresence parcelManager = null;

            // Must have presences
            if (!m_scene.TryGetScenePresence(target, out targetAvatar) ||
                !m_scene.TryGetScenePresence(client.AgentId, out parcelManager))
                return;

            // Cannot eject estate managers or gods
            if (m_scene.Permissions.IsAdministrator(target))
                return;

            // Check if you even have permission to do this
            ILandObject land = m_scene.LandChannel.GetLandObject(targetAvatar.AbsolutePosition.X, targetAvatar.AbsolutePosition.Y);
            if (!m_scene.Permissions.CanEditParcelProperties(client.AgentId, land, GroupPowers.LandEjectAndFreeze, true) &&
                !m_scene.Permissions.IsAdministrator(client.AgentId))
                return;

            Vector3 pos = m_scene.GetNearestAllowedPosition(targetAvatar, land);

            targetAvatar.TeleportOnEject(pos);
            targetAvatar.ControllingClient.SendAlertMessage("You have been ejected by " + parcelManager.Firstname + " " + parcelManager.Lastname);
            parcelManager.ControllingClient.SendAlertMessage("Avatar Ejected.");

            if ((flags & 1) != 0) // Ban TODO: Remove magic number
            {
                LandAccessEntry entry = new LandAccessEntry();
                entry.AgentID = targetAvatar.UUID;
                entry.Flags = AccessList.Ban;
                entry.Expires = 0; // Perm

                land.LandData.ParcelAccessList.Add(entry);
            }
        }

        /// <summary>
        /// Sets the Home Point.   The LoginService uses this to know where to put a user when they log-in
        /// </summary>
        /// <param name="remoteClient"></param>
        /// <param name="regionHandle"></param>
        /// <param name="position"></param>
        /// <param name="lookAt"></param>
        /// <param name="flags"></param>
        public virtual void ClientOnSetHome(IClientAPI remoteClient, ulong regionHandle, Vector3 position, Vector3 lookAt, uint flags)
        {
            // Let's find the parcel in question
            ILandObject land = landChannel.GetLandObject(position);
            if (land == null || m_scene.GridUserService == null)
            {
                m_Dialog.SendAlertToUser(remoteClient, "Set Home request failed.");
                return;
            }

            // Gather some data
            ulong gpowers = remoteClient.GetGroupPowers(land.LandData.GroupID);
            SceneObjectGroup telehub = null;
            if (m_scene.RegionInfo.RegionSettings.TelehubObject != UUID.Zero)
                // Does the telehub exist in the scene?
                telehub = m_scene.GetSceneObjectGroup(m_scene.RegionInfo.RegionSettings.TelehubObject);

            // Can the user set home here?
            if (// Required: local user; foreign users cannot set home
                m_scene.UserManagementModule.IsLocalGridUser(remoteClient.AgentId) &&
                (// (a) gods and land managers can set home
                 m_scene.Permissions.IsAdministrator(remoteClient.AgentId) ||
                 m_scene.Permissions.IsGod(remoteClient.AgentId) ||
                 // (b) land owners can set home
                 remoteClient.AgentId == land.LandData.OwnerID ||
                 // (c) members of the land-associated group in roles that can set home
                 ((gpowers & (ulong)GroupPowers.AllowSetHome) == (ulong)GroupPowers.AllowSetHome) ||
                 // (d) parcels with telehubs can be the home of anyone
                 (telehub != null && land.ContainsPoint((int)telehub.AbsolutePosition.X, (int)telehub.AbsolutePosition.Y))))
            {
                string userId;
                UUID test;
                if (!m_scene.UserManagementModule.GetUserUUI(remoteClient.AgentId, out userId))
                {
                    /* Do not set a home position in this grid for a HG visitor */
                    m_Dialog.SendAlertToUser(remoteClient, "Set Home request failed. (User Lookup)");
                }
                else if (!UUID.TryParse(userId, out test))
                {
                    m_Dialog.SendAlertToUser(remoteClient, "Set Home request failed. (HG visitor)");
                }
                else if (m_scene.GridUserService.SetHome(userId, land.RegionUUID, position, lookAt))
                {
                    // FUBAR ALERT: this needs to be "Home position set." so the viewer saves a home-screenshot.
                    m_Dialog.SendAlertToUser(remoteClient, "Home position set.");
                }
                else
                {
                    m_Dialog.SendAlertToUser(remoteClient, "Set Home request failed.");
                }
            }
            else
                m_Dialog.SendAlertToUser(remoteClient, "You are not allowed to set your home location in this parcel.");
        }

        protected void RegisterCommands()
        {
            ICommands commands = MainConsole.Instance.Commands;

            commands.AddCommand(
                "Land", false, "land clear",
                "land clear",
                "Clear all the parcels from the region.",
                "Command will ask for confirmation before proceeding.",
                HandleClearCommand);

            commands.AddCommand(
                "Land", false, "land show",
                "land show [<local-land-id>]",
                "Show information about the parcels on the region.",
                "If no local land ID is given, then summary information about all the parcels is shown.\n"
                    + "If a local land ID is given then full information about that parcel is shown.",
                HandleShowCommand);
        }

        protected void HandleClearCommand(string module, string[] args)
        {
            if (!(MainConsole.Instance.ConsoleScene == null || MainConsole.Instance.ConsoleScene == m_scene))
                return;

            string response = MainConsole.Instance.Prompt(
                string.Format(
                    "Are you sure that you want to clear all land parcels from {0} (y or n)", m_scene.Name),
                "n");

            if (response.ToLower() == "y")
            {
                Clear(true);
                MainConsole.Instance.Output("Cleared all parcels from {0}", null, m_scene.Name);
            }
            else
            {
                MainConsole.Instance.Output("Aborting clear of all parcels from {0}", null, m_scene.Name);
            }
        }

        protected void HandleShowCommand(string module, string[] args)
        {
            if (!(MainConsole.Instance.ConsoleScene == null || MainConsole.Instance.ConsoleScene == m_scene))
                return;

            StringBuilder report = new StringBuilder();

            if (args.Length <= 2)
            {
                AppendParcelsSummaryReport(report);
            }
            else
            {
                int landLocalId;

                if (!ConsoleUtil.TryParseConsoleInt(MainConsole.Instance, args[2], out landLocalId))
                    return;

                ILandObject lo = null;

                lock (m_landList)
                {
                    if (!m_landList.TryGetValue(landLocalId, out lo))
                    {
                        MainConsole.Instance.Output("No parcel found with local ID {0}", null, landLocalId);
                        return;
                    }
                }

                AppendParcelReport(report, lo);
            }

            MainConsole.Instance.Output(report.ToString());
        }

        private void AppendParcelsSummaryReport(StringBuilder report)
        {
            report.AppendFormat("Land information for {0}\n", m_scene.Name);

            ConsoleDisplayTable cdt = new ConsoleDisplayTable();
            cdt.AddColumn("Parcel Name", ConsoleDisplayUtil.ParcelNameSize);
            cdt.AddColumn("ID", 3);
            cdt.AddColumn("Area", 6);
            cdt.AddColumn("Starts", ConsoleDisplayUtil.VectorSize);
            cdt.AddColumn("Ends", ConsoleDisplayUtil.VectorSize);
            cdt.AddColumn("Owner", ConsoleDisplayUtil.UserNameSize);

            lock (m_landList)
            {
                foreach (ILandObject lo in m_landList.Values)
                {
                    LandData ld = lo.LandData;
                    string ownerName;
                    if (ld.IsGroupOwned)
                    {
                        GroupRecord rec = m_groupManager.GetGroupRecord(ld.GroupID);
                        ownerName = (rec != null) ? rec.GroupName : "Unknown Group";
                    }
                    else
                    {
                        ownerName = m_userManager.GetUserName(ld.OwnerID);
                    }
                    cdt.AddRow(
                        ld.Name, ld.LocalID, ld.Area, lo.StartPoint, lo.EndPoint, ownerName);
                }
            }

            report.Append(cdt.ToString());
        }

        private void AppendParcelReport(StringBuilder report, ILandObject lo)
        {
            LandData ld = lo.LandData;

            ConsoleDisplayList cdl = new ConsoleDisplayList();
            cdl.AddRow("Parcel name", ld.Name);
            cdl.AddRow("Local ID", ld.LocalID);
            cdl.AddRow("Description", ld.Description);
            cdl.AddRow("Snapshot ID", ld.SnapshotID);
            cdl.AddRow("Area", ld.Area);
            cdl.AddRow("AABB Min", ld.AABBMin);
            cdl.AddRow("AABB Max", ld.AABBMax);
            string ownerName;
            if (ld.IsGroupOwned)
            {
                GroupRecord rec = m_groupManager.GetGroupRecord(ld.GroupID);
                ownerName = (rec != null) ? rec.GroupName : "Unknown Group";
            }
            else
            {
                ownerName = m_userManager.GetUserName(ld.OwnerID);
            }
            cdl.AddRow("Owner", ownerName);
            cdl.AddRow("Is group owned?", ld.IsGroupOwned);
            cdl.AddRow("GroupID", ld.GroupID);

            cdl.AddRow("Status", ld.Status);
            cdl.AddRow("Flags", (ParcelFlags)ld.Flags);

            cdl.AddRow("Landing Type", (LandingType)ld.LandingType);
            cdl.AddRow("User Location", ld.UserLocation);
            cdl.AddRow("User look at", ld.UserLookAt);

            cdl.AddRow("Other clean time", ld.OtherCleanTime);

            cdl.AddRow("Max Prims", lo.GetParcelMaxPrimCount());
            cdl.AddRow("Simwide Max Prims (owner)", lo.GetSimulatorMaxPrimCount());
            IPrimCounts pc = lo.PrimCounts;
            cdl.AddRow("Owner Prims", pc.Owner);
            cdl.AddRow("Group Prims", pc.Group);
            cdl.AddRow("Other Prims", pc.Others);
            cdl.AddRow("Selected Prims", pc.Selected);
            cdl.AddRow("Total Prims", pc.Total);
            cdl.AddRow("SimWide Prims (owner)", pc.Simulator);

            cdl.AddRow("Music URL", ld.MusicURL);
            cdl.AddRow("Obscure Music", ld.ObscureMusic);

            cdl.AddRow("Media ID", ld.MediaID);
            cdl.AddRow("Media Autoscale", Convert.ToBoolean(ld.MediaAutoScale));
            cdl.AddRow("Media URL", ld.MediaURL);
            cdl.AddRow("Media Type", ld.MediaType);
            cdl.AddRow("Media Description", ld.MediaDescription);
            cdl.AddRow("Media Width", ld.MediaWidth);
            cdl.AddRow("Media Height", ld.MediaHeight);
            cdl.AddRow("Media Loop", ld.MediaLoop);
            cdl.AddRow("Obscure Media", ld.ObscureMedia);

            cdl.AddRow("Parcel Category", ld.Category);

            cdl.AddRow("Claim Date", ld.ClaimDate);
            cdl.AddRow("Claim Price", ld.ClaimPrice);
            cdl.AddRow("Pass Hours", ld.PassHours);
            cdl.AddRow("Pass Price", ld.PassPrice);

            cdl.AddRow("Auction ID", ld.AuctionID);
            cdl.AddRow("Authorized Buyer ID", ld.AuthBuyerID);
            cdl.AddRow("Sale Price", ld.SalePrice);

            cdl.AddToStringBuilder(report);
        }
    }
}
