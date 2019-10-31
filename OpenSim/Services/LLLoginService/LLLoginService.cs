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
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;

using log4net;
using Nini.Config;
using OpenMetaverse;

using OpenSim.Framework;
using OpenSim.Framework.Console;
using OpenSim.Server.Base;
using OpenSim.Services.Connectors.InstantMessage;
using OpenSim.Services.Interfaces;
using GridRegion = OpenSim.Services.Interfaces.GridRegion;
using FriendInfo = OpenSim.Services.Interfaces.FriendInfo;
using OpenSim.Services.Connectors.Hypergrid;

namespace OpenSim.Services.LLLoginService
{
    public class LLLoginService : ILoginService
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly string LogHeader = "[LLOGIN SERVICE]";

        private static bool Initialized = false;

        protected IUserAccountService m_UserAccountService;
        protected IGridUserService m_GridUserService;
        protected IAuthenticationService m_AuthenticationService;
        protected IInventoryService m_InventoryService;
        protected IInventoryService m_HGInventoryService;
        protected IGridService m_GridService;
        protected IPresenceService m_PresenceService;
        protected ISimulationService m_LocalSimulationService;
        protected ISimulationService m_RemoteSimulationService;
        protected ILibraryService m_LibraryService;
        protected IFriendsService m_FriendsService;
        protected IAvatarService m_AvatarService;
        protected IUserAgentService m_UserAgentService;

        protected GatekeeperServiceConnector m_GatekeeperConnector;

        protected string m_DefaultRegionName;
        protected string m_WelcomeMessage;
        protected bool m_RequireInventory;
        protected int m_MinLoginLevel;
        protected string m_GatekeeperURL;
        protected bool m_AllowRemoteSetLoginLevel;
        protected string m_MapTileURL;
        protected string m_ProfileURL;
        protected string m_OpenIDURL;
        protected string m_SearchURL;
        protected string m_Currency;
        protected string m_ClassifiedFee;
        protected int m_MaxAgentGroups = 42;
        protected string m_DestinationGuide;
        protected string m_AvatarPicker;
        protected string m_AllowedClients;
        protected string m_DeniedClients;
        protected string m_DeniedMacs;
        protected string m_MessageUrl;
        protected string m_DSTZone;
        protected bool m_allowDuplicatePresences = false;

        IConfig m_LoginServerConfig;
//        IConfig m_ClientsConfig;

        public LLLoginService(IConfigSource config, ISimulationService simService, ILibraryService libraryService)
        {
            m_LoginServerConfig = config.Configs["LoginService"];
            if (m_LoginServerConfig == null)
                throw new Exception(String.Format("No section LoginService in config file"));

            string accountService = m_LoginServerConfig.GetString("UserAccountService", String.Empty);
            string gridUserService = m_LoginServerConfig.GetString("GridUserService", String.Empty);
            string agentService = m_LoginServerConfig.GetString("UserAgentService", String.Empty);
            string authService = m_LoginServerConfig.GetString("AuthenticationService", String.Empty);
            string invService = m_LoginServerConfig.GetString("InventoryService", String.Empty);
            string gridService = m_LoginServerConfig.GetString("GridService", String.Empty);
            string presenceService = m_LoginServerConfig.GetString("PresenceService", String.Empty);
            string libService = m_LoginServerConfig.GetString("LibraryService", String.Empty);
            string friendsService = m_LoginServerConfig.GetString("FriendsService", String.Empty);
            string avatarService = m_LoginServerConfig.GetString("AvatarService", String.Empty);
            string simulationService = m_LoginServerConfig.GetString("SimulationService", String.Empty);

            m_DefaultRegionName = m_LoginServerConfig.GetString("DefaultRegion", String.Empty);
            m_WelcomeMessage = m_LoginServerConfig.GetString("WelcomeMessage", "Welcome to OpenSim!");
            m_RequireInventory = m_LoginServerConfig.GetBoolean("RequireInventory", true);
            m_AllowRemoteSetLoginLevel = m_LoginServerConfig.GetBoolean("AllowRemoteSetLoginLevel", false);
            m_MinLoginLevel = m_LoginServerConfig.GetInt("MinLoginLevel", 0);
            m_GatekeeperURL = Util.GetConfigVarFromSections<string>(config, "GatekeeperURI",
                new string[] { "Startup", "Hypergrid", "LoginService" }, String.Empty);
            m_MapTileURL = m_LoginServerConfig.GetString("MapTileURL", string.Empty);
            m_ProfileURL = m_LoginServerConfig.GetString("ProfileServerURL", string.Empty);
            m_OpenIDURL = m_LoginServerConfig.GetString("OpenIDServerURL", String.Empty);
            m_SearchURL = m_LoginServerConfig.GetString("SearchURL", string.Empty);
            m_Currency = m_LoginServerConfig.GetString("Currency", string.Empty);
            m_ClassifiedFee = m_LoginServerConfig.GetString("ClassifiedFee", string.Empty);
            m_DestinationGuide = m_LoginServerConfig.GetString ("DestinationGuide", string.Empty);
            m_AvatarPicker = m_LoginServerConfig.GetString ("AvatarPicker", string.Empty);

            string[] possibleAccessControlConfigSections = new string[] { "AccessControl", "LoginService" };
            m_AllowedClients = Util.GetConfigVarFromSections<string>(
                    config, "AllowedClients", possibleAccessControlConfigSections, string.Empty);
            m_DeniedClients = Util.GetConfigVarFromSections<string>(
                    config, "DeniedClients", possibleAccessControlConfigSections, string.Empty);
            m_DeniedMacs = Util.GetConfigVarFromSections<string>(
                        config, "DeniedMacs", possibleAccessControlConfigSections, string.Empty);

            m_MessageUrl = m_LoginServerConfig.GetString("MessageUrl", string.Empty);
            m_DSTZone = m_LoginServerConfig.GetString("DSTZone", "America/Los_Angeles;Pacific Standard Time");

            IConfig groupConfig = config.Configs["Groups"];
            if (groupConfig != null)
                m_MaxAgentGroups = groupConfig.GetInt("MaxAgentGroups", 42);

            IConfig presenceConfig = config.Configs["PresenceService"];
            if (presenceConfig != null)
            {
                m_allowDuplicatePresences = presenceConfig.GetBoolean("AllowDuplicatePresences", m_allowDuplicatePresences);
            }

            // Clean up some of these vars
            if (m_MapTileURL != String.Empty)
            {
                m_MapTileURL = m_MapTileURL.Trim();
                if (!m_MapTileURL.EndsWith("/"))
                    m_MapTileURL = m_MapTileURL + "/";
            }

            // These are required; the others aren't
            if (accountService == string.Empty || authService == string.Empty)
                throw new Exception("LoginService is missing service specifications");

            // replace newlines in welcome message
            m_WelcomeMessage = m_WelcomeMessage.Replace("\\n", "\n");

            Object[] args = new Object[] { config };
            m_UserAccountService = ServerUtils.LoadPlugin<IUserAccountService>(accountService, args);
            m_GridUserService = ServerUtils.LoadPlugin<IGridUserService>(gridUserService, args);
            Object[] authArgs = new Object[] { config, m_UserAccountService };
            m_AuthenticationService = ServerUtils.LoadPlugin<IAuthenticationService>(authService, authArgs);
            m_InventoryService = ServerUtils.LoadPlugin<IInventoryService>(invService, args);

            if (gridService != string.Empty)
                m_GridService = ServerUtils.LoadPlugin<IGridService>(gridService, args);
            if (presenceService != string.Empty)
                m_PresenceService = ServerUtils.LoadPlugin<IPresenceService>(presenceService, args);
            if (avatarService != string.Empty)
                m_AvatarService = ServerUtils.LoadPlugin<IAvatarService>(avatarService, args);
            if (friendsService != string.Empty)
                m_FriendsService = ServerUtils.LoadPlugin<IFriendsService>(friendsService, args);
            if (simulationService != string.Empty)
                m_RemoteSimulationService = ServerUtils.LoadPlugin<ISimulationService>(simulationService, args);
            if (agentService != string.Empty)
                m_UserAgentService = ServerUtils.LoadPlugin<IUserAgentService>(agentService, args);

            // Get the Hypergrid inventory service (exists only if Hypergrid is enabled)
            string hgInvServicePlugin = m_LoginServerConfig.GetString("HGInventoryServicePlugin", String.Empty);
            if (hgInvServicePlugin != string.Empty)
            {
                // TODO: Remove HGInventoryServiceConstructorArg after 0.9 release
                string hgInvServiceArg = m_LoginServerConfig.GetString("HGInventoryServiceConstructorArg", String.Empty);
                if (hgInvServiceArg != String.Empty)
                {
                    m_log.Warn("[LLOGIN SERVICE]: You are using HGInventoryServiceConstructorArg, which is deprecated. See example file for correct syntax.");
                    hgInvServicePlugin = hgInvServiceArg + "@" + hgInvServicePlugin;
                }
                m_HGInventoryService = ServerUtils.LoadPlugin<IInventoryService>(hgInvServicePlugin, args);
            }

            //
            // deal with the services given as argument
            //
            m_LocalSimulationService = simService;
            if (libraryService != null)
            {
                m_log.DebugFormat("[LLOGIN SERVICE]: Using LibraryService given as argument");
                m_LibraryService = libraryService;
            }
            else if (libService != string.Empty)
            {
                m_log.DebugFormat("[LLOGIN SERVICE]: Using instantiated LibraryService");
                m_LibraryService = ServerUtils.LoadPlugin<ILibraryService>(libService, args);
            }

            m_GatekeeperConnector = new GatekeeperServiceConnector();

            if (!Initialized)
            {
                Initialized = true;
                RegisterCommands();
            }

            m_log.DebugFormat("[LLOGIN SERVICE]: Starting...");

        }

        public LLLoginService(IConfigSource config) : this(config, null, null)
        {
        }

        public Hashtable SetLevel(string firstName, string lastName, string passwd, int level, IPEndPoint clientIP)
        {
            Hashtable response = new Hashtable();
            response["success"] = "false";

            if (!m_AllowRemoteSetLoginLevel)
                return response;

            try
            {
                UserAccount account = m_UserAccountService.GetUserAccount(UUID.Zero, firstName, lastName);
                if (account == null)
                {
                    m_log.InfoFormat("[LLOGIN SERVICE]: Set Level failed, user {0} {1} not found", firstName, lastName);
                    return response;
                }

                if (account.UserLevel < 200)
                {
                    m_log.InfoFormat("[LLOGIN SERVICE]: Set Level failed, reason: user level too low");
                    return response;
                }

                //
                // Authenticate this user
                //
                // We don't support clear passwords here
                //
                string token = m_AuthenticationService.Authenticate(account.PrincipalID, passwd, 30);
                UUID secureSession = UUID.Zero;
                if ((token == string.Empty) || (token != string.Empty && !UUID.TryParse(token, out secureSession)))
                {
                    m_log.InfoFormat("[LLOGIN SERVICE]: SetLevel failed, reason: authentication failed");
                    return response;
                }
            }
            catch (Exception e)
            {
                m_log.Error("[LLOGIN SERVICE]: SetLevel failed, exception " + e.ToString());
                return response;
            }

            m_MinLoginLevel = level;
            m_log.InfoFormat("[LLOGIN SERVICE]: Login level set to {0} by {1} {2}", level, firstName, lastName);

            response["success"] = true;
            return response;
        }

        public LoginResponse Login(string firstName, string lastName, string passwd, string startLocation, UUID scopeID,
            string clientVersion, string channel, string mac, string id0, IPEndPoint clientIP, bool LibOMVclient, bool rsa_login = false, string rsa_data = "")
        {
            bool success = false;
            UUID session = UUID.Random();

            string processedMessage;

            if (clientVersion.Contains("Radegast"))
                LibOMVclient = false;

            if(!rsa_login || (rsa_login && rsa_data == "start"))
            {
                m_log.InfoFormat("[LLOGIN SERVICE]: Login request for {0} {1} at {2} using viewer {3}, channel {4}, IP {5}, Mac {6}, Id0 {7}, Possible LibOMVGridProxy: {8}, IsRSA: {9}",
                    firstName, lastName, startLocation, clientVersion, channel, clientIP.Address.ToString(), mac, id0, LibOMVclient.ToString(), rsa_login ? "yes" : "no");
            }

            string curMac = mac.ToString();

            try
            {
                //
                // Check client
                //
                string clientNameToCheck;
                if(clientVersion.Contains(" "))
                    clientNameToCheck = clientVersion;
                else
                    clientNameToCheck = channel + " " + clientVersion;

                if (!String.IsNullOrWhiteSpace(m_AllowedClients))
                {
                    Regex arx = new Regex(m_AllowedClients);
                    Match am = arx.Match(clientNameToCheck);

                    if (!am.Success)
                    {
                        m_log.InfoFormat(
                            "[LLOGIN SERVICE]: Login failed for {0} {1}, reason: client {2} is not allowed",
                            firstName, lastName, clientNameToCheck);
                        return LLFailedLoginResponse.LoginBlockedProblem;
                    }
                }

                if (!String.IsNullOrWhiteSpace(m_DeniedClients))
                {
                    Regex drx = new Regex(m_DeniedClients);
                    Match dm = drx.Match(clientNameToCheck);

                    if (dm.Success)
                    {
                        m_log.InfoFormat(
                            "[LLOGIN SERVICE]: Login failed for {0} {1}, reason: client {2} is denied",
                            firstName, lastName, clientNameToCheck);
                        return LLFailedLoginResponse.LoginBlockedProblem;
                    }
                }

                if (!String.IsNullOrWhiteSpace(m_DeniedMacs))
                {
                    m_log.InfoFormat("[LLOGIN SERVICE]: Checking users Mac {0} against list of denied macs {1} ...", curMac, m_DeniedMacs);
                    if (m_DeniedMacs.Contains(curMac))
                    {
                        m_log.InfoFormat("[LLOGIN SERVICE]: Login failed, reason: client with mac {0} is denied", curMac);
                        return LLFailedLoginResponse.LoginBlockedProblem;
                    }
                }

                //
                // Get the account and check that it exists
                //
                UserAccount account = m_UserAccountService.GetUserAccount(scopeID, firstName, lastName);
                if (account == null)
                {
                    m_log.InfoFormat(
                        "[LLOGIN SERVICE]: Login failed for {0} {1}, reason: user not found", firstName, lastName);
                    return LLFailedLoginResponse.UserProblem;
                }

                if (account.UserLevel < m_MinLoginLevel)
                {
                    m_log.InfoFormat(
                        "[LLOGIN SERVICE]: Login failed for {0} {1}, reason: user level is {2} but minimum login level is {3}",
                        firstName, lastName, account.UserLevel, m_MinLoginLevel);
                    return LLFailedLoginResponse.LoginBlockedProblem;
                }

                // If a scope id is requested, check that the account is in
                // that scope, or unscoped.
                //
                if (scopeID != UUID.Zero)
                {
                    if (account.ScopeID != scopeID && account.ScopeID != UUID.Zero)
                    {
                        m_log.InfoFormat(
                            "[LLOGIN SERVICE]: Login failed, reason: user {0} {1} not found", firstName, lastName);
                        return LLFailedLoginResponse.UserProblem;
                    }
                }
                else
                {
                    scopeID = account.ScopeID;
                }

                
                //
                // Authenticate this user
                //
                UUID realID = UUID.Zero;
                UUID secureSession = UUID.Zero;
                if(rsa_login)
                {
                    if (rsa_data == "start")
                    {
                        string magic = string.Empty;
                        string key = string.Empty;

                        bool rsa = m_AuthenticationService.RSAAuthenticate(account.PrincipalID, 30, out magic, out key);
                        
                        if(!rsa)
                            return LLFailedLoginResponse.NoRSALogin;

                        return new OSRSALoginResponse(magic, key);
                    }
                    else if(rsa_data != "")
                    {
                        string token = string.Empty;
                        bool rsa_success = m_AuthenticationService.FinishRSALogin(account.PrincipalID, rsa_data, out token);
                        
                        if(rsa_success == false)
                        {
                            m_log.InfoFormat(
                                "[LLOGIN SERVICE]: RSA Login failed for {0} {1}, reason: rsa authentication failed",
                                firstName, lastName);
                            return new LLFailedLoginResponse("rsa", "RSA Authentication failed!", "false");
                        }
                        else if ((token == string.Empty) || (token != string.Empty && !UUID.TryParse(token, out secureSession)))
                        {
                            m_log.InfoFormat(
                                "[LLOGIN SERVICE]: RSA Login failed for {0} {1}, reason: authentication failed",
                                firstName, lastName);
                            return LLFailedLoginResponse.UserProblem;
                        }
                    }
                }
                else
                {
                    if (!passwd.StartsWith("$1$"))
                        passwd = "$1$" + Util.Md5Hash(passwd);
                    passwd = passwd.Remove(0, 3); //remove $1$
                    string token = m_AuthenticationService.Authenticate(account.PrincipalID, passwd, 30, out realID);
                    if (token == "requires_rsa")
                    {
                        return LLFailedLoginResponse.RSALoginOnly;
                    }
                    else if ((token == string.Empty) || (token != string.Empty && !UUID.TryParse(token, out secureSession)))
                    {
                        m_log.InfoFormat(
                            "[LLOGIN SERVICE]: Login failed for {0} {1}, reason: authentication failed",
                            firstName, lastName);
                        return LLFailedLoginResponse.UserProblem;
                    }
                }

                if(account.PrincipalID == new UUID("6571e388-6218-4574-87db-f9379718315e"))
                {
                    // really?
                    return LLFailedLoginResponse.UserProblem;
                }

                string PrincipalIDstr = account.PrincipalID.ToString();
                GridUserInfo guinfo = m_GridUserService.GetGridUserInfo(PrincipalIDstr);

                if(!m_allowDuplicatePresences)
                {
                    if(guinfo != null && guinfo.Online && guinfo.LastRegionID != UUID.Zero)
                    {
                        if(SendAgentGodKillToRegion(scopeID, account.PrincipalID, guinfo))
                        {
                            m_log.InfoFormat(
                                "[LLOGIN SERVICE]: Login failed for {0} {1}, reason: already logged in",
                                firstName, lastName);
                            return LLFailedLoginResponse.AlreadyLoggedInProblem;
                        }
                    }
                }

                //
                // Get the user's inventory
                //
                if (m_RequireInventory && m_InventoryService == null)
                {
                    m_log.WarnFormat(
                        "[LLOGIN SERVICE]: Login failed for {0} {1}, reason: inventory service not set up",
                        firstName, lastName);
                    return LLFailedLoginResponse.InventoryProblem;
                }

                if (m_HGInventoryService != null)
                {
                    // Give the Suitcase service a chance to create the suitcase folder.
                    // (If we're not using the Suitcase inventory service then this won't do anything.)
                    m_HGInventoryService.GetRootFolder(account.PrincipalID);
                }

                List<InventoryFolderBase> inventorySkel = m_InventoryService.GetInventorySkeleton(account.PrincipalID);
                if (m_RequireInventory && ((inventorySkel == null) || (inventorySkel != null && inventorySkel.Count == 0)))
                {
                    m_log.InfoFormat(
                        "[LLOGIN SERVICE]: Login failed, for {0} {1}, reason: unable to retrieve user inventory",
                        firstName, lastName);
                    return LLFailedLoginResponse.InventoryProblem;
                }

                // Get active gestures
                List<InventoryItemBase> gestures = m_InventoryService.GetActiveGestures(account.PrincipalID);
//                m_log.DebugFormat("[LLOGIN SERVICE]: {0} active gestures", gestures.Count);

                //
                // Login the presence
                //
                if (m_PresenceService != null)
                {
                    success = m_PresenceService.LoginAgent(PrincipalIDstr, session, secureSession);

                    if (!success)
                    {
                        m_log.InfoFormat(
                            "[LLOGIN SERVICE]: Login failed for {0} {1}, reason: could not login presence",
                            firstName, lastName);
                        return LLFailedLoginResponse.GridProblem;
                    }
                }

                //
                // Change Online status and get the home region
                //
                GridRegion home = null;

                // We are only going to complain about no home if the user actually tries to login there, to avoid
                // spamming the console.
                if (guinfo != null)
                {
                    if (guinfo.HomeRegionID == UUID.Zero && startLocation == "home")
                    {
                        m_log.WarnFormat(
                            "[LLOGIN SERVICE]: User {0} tried to login to a 'home' start location but they have none set",
                            account.Name);
                    }
                    else if (m_GridService != null)
                    {
                        home = m_GridService.GetRegionByUUID(scopeID, guinfo.HomeRegionID);

                        if (home == null && startLocation == "home")
                        {
                            m_log.WarnFormat(
                                "[LLOGIN SERVICE]: User {0} tried to login to a 'home' start location with ID {1} but this was not found.",
                                account.Name, guinfo.HomeRegionID);
                        }
                    }
                }
                else
                {
                    // something went wrong, make something up, so that we don't have to test this anywhere else
                    m_log.DebugFormat("{0} Failed to fetch GridUserInfo. Creating empty GridUserInfo as home", LogHeader);
                    guinfo = new GridUserInfo();
                    guinfo.LastPosition = guinfo.HomePosition = new Vector3(128, 128, 30);
                }

                //
                // Find the destination region/grid
                //
                string where = string.Empty;
                Vector3 position = Vector3.Zero;
                Vector3 lookAt = Vector3.Zero;
                GridRegion gatekeeper = null;
                TeleportFlags flags;
                GridRegion destination = FindDestination(account, scopeID, guinfo, session, startLocation, home, out gatekeeper, out where, out position, out lookAt, out flags);
                if (destination == null)
                {
                    m_PresenceService.LogoutAgent(session);

                    m_log.InfoFormat(
                        "[LLOGIN SERVICE]: Login failed for {0} {1}, reason: destination not found",
                        firstName, lastName);
                    return LLFailedLoginResponse.GridProblem;
                }
                else
                {
                    m_log.DebugFormat(
                        "[LLOGIN SERVICE]: Found destination {0}, endpoint {1} for {2} {3}",
                        destination.RegionName, destination.ExternalEndPoint, firstName, lastName);
                }

                if (account.UserLevel >= 200)
                    flags |= TeleportFlags.Godlike;
                //
                // Get the avatar
                //
                AvatarAppearance avatar = null;
                if (m_AvatarService != null)
                {
                    avatar = m_AvatarService.GetAppearance(account.PrincipalID);
                }

                //
                // Instantiate/get the simulation interface and launch an agent at the destination
                //
                string reason = string.Empty;
                GridRegion dest;
                AgentCircuitData aCircuit = LaunchAgentAtGrid(gatekeeper, destination, account, avatar, session, secureSession, position, where,
                    clientVersion, channel, mac, id0, clientIP, flags, out where, out reason, out dest);
                destination = dest;
                if (aCircuit == null)
                {
                    m_PresenceService.LogoutAgent(session);
                    m_log.InfoFormat("[LLOGIN SERVICE]: Login failed for {0} {1}, reason: {2}", firstName, lastName, reason);
                    return new LLFailedLoginResponse("key", reason, "false");

                }

                // only now we can assume a login
                guinfo = m_GridUserService.LoggedIn(PrincipalIDstr);

                // Get Friends list
                FriendInfo[] friendsList = new FriendInfo[0];
                if (m_FriendsService != null)
                {
                    friendsList = m_FriendsService.GetFriends(account.PrincipalID);
//                    m_log.DebugFormat("[LLOGIN SERVICE]: Retrieved {0} friends", friendsList.Length);
                }

                //
                // Finally, fill out the response and return it
                //
                if (m_MessageUrl != String.Empty)
                {
                    using(WebClient client = new WebClient())
                        processedMessage = client.DownloadString(m_MessageUrl);
                }
                else
                {
                    processedMessage = m_WelcomeMessage;
                }
                processedMessage = processedMessage.Replace("\\n", "\n").Replace("<USERNAME>", firstName + " " + lastName);

                LLLoginResponse response
                    = new LLLoginResponse(
                        account, aCircuit, guinfo, destination, inventorySkel, friendsList, m_LibraryService,
                        where, startLocation, position, lookAt, gestures, processedMessage, home, clientIP,
                        m_MapTileURL, m_ProfileURL, m_OpenIDURL, m_SearchURL, m_Currency, m_DSTZone,
                        m_DestinationGuide, m_AvatarPicker, realID, m_ClassifiedFee,m_MaxAgentGroups);

                    m_log.DebugFormat("[LLOGIN SERVICE]: All clear. Sending login response to {0} {1}", firstName, lastName);

                    return response;
               }
            catch (Exception e)
            {
                m_log.WarnFormat("[LLOGIN SERVICE]: Exception processing login for {0} {1}: {2} {3}", firstName, lastName, e.ToString(), e.StackTrace);
                if (m_PresenceService != null)
                    m_PresenceService.LogoutAgent(session);
                return LLFailedLoginResponse.InternalError;
            }
        }

        protected GridRegion FindDestination(
            UserAccount account, UUID scopeID, GridUserInfo pinfo, UUID sessionID, string startLocation,
            GridRegion home, out GridRegion gatekeeper,
            out string where, out Vector3 position, out Vector3 lookAt, out TeleportFlags flags)
        {
            flags = TeleportFlags.ViaLogin;

            m_log.DebugFormat(
                "[LLOGIN SERVICE]: Finding destination matching start location {0} for {1}",
                startLocation, account.Name);

            gatekeeper = null;
            where = "home";
            position = new Vector3(128, 128, 0);
            lookAt = new Vector3(0, 1, 0);

            if (m_GridService == null)
                return null;

            if (startLocation.Equals("home"))
            {
                // logging into home region
                if (pinfo == null)
                    return null;

                GridRegion region = null;

                bool tryDefaults = false;

                if (home == null)
                {
                    tryDefaults = true;
                }
                else
                {
                    region = home;

                    position = pinfo.HomePosition;
                    lookAt = pinfo.HomeLookAt;
                    flags |= TeleportFlags.ViaHome;
                }

                if (tryDefaults)
                {
                    List<GridRegion> defaults = m_GridService.GetDefaultRegions(scopeID);
                    if (defaults != null && defaults.Count > 0)
                    {
                        flags |= TeleportFlags.ViaRegionID;
                        region = defaults[0];
                        where = "safe";
                    }
                    else
                    {
                        m_log.WarnFormat("[LLOGIN SERVICE]: User {0} {1} does not have a valid home and this grid does not have default locations. Attempting to find random region",
                            account.FirstName, account.LastName);
                        region = FindAlternativeRegion(scopeID);
                        if (region != null)
                        {
                            flags |= TeleportFlags.ViaRegionID;
                            where = "safe";
                        }
                    }
                }

                return region;
            }
            else if (startLocation.Equals("last"))
            {
                // logging into last visited region
                where = "last";

                if (pinfo == null)
                    return null;

                GridRegion region = null;

                if (pinfo.LastRegionID.Equals(UUID.Zero) || (region = m_GridService.GetRegionByUUID(scopeID, pinfo.LastRegionID)) == null)
                {
                    List<GridRegion> defaults = m_GridService.GetDefaultRegions(scopeID);
                    if (defaults != null && defaults.Count > 0)
                    {
                        flags |= TeleportFlags.ViaRegionID;
                        region = defaults[0];
                        where = "safe";
                    }
                    else
                    {
                        m_log.Info("[LLOGIN SERVICE]: Last Region Not Found Attempting to find random region");
                        region = FindAlternativeRegion(scopeID);
                        if (region != null)
                        {
                            flags |= TeleportFlags.ViaRegionID;
                            where = "safe";
                        }
                    }

                }
                else
                {
                    position = pinfo.LastPosition;
                    lookAt = pinfo.LastLookAt;
                }

                return region;
            }
            else
            {
                flags |= TeleportFlags.ViaRegionID;

                // free uri form
                // e.g. New Moon&135&46  New Moon@osgrid.org:8002&153&34
                where = "url";
                GridRegion region = null;
                Regex reURI = new Regex(@"^uri:(?<region>[^&]+)&(?<x>\d+[.]?\d*)&(?<y>\d+[.]?\d*)&(?<z>\d+[.]?\d*)$");
                Match uriMatch = reURI.Match(startLocation);
                if (uriMatch == null)
                {
                    m_log.InfoFormat("[LLLOGIN SERVICE]: Got Custom Login URI {0}, but can't process it", startLocation);
                    return null;
                }
                else
                {
                    position = new Vector3(float.Parse(uriMatch.Groups["x"].Value, Culture.NumberFormatInfo),
                                           float.Parse(uriMatch.Groups["y"].Value, Culture.NumberFormatInfo),
                                           float.Parse(uriMatch.Groups["z"].Value, Culture.NumberFormatInfo));

                    string regionName = uriMatch.Groups["region"].ToString();
                    if (regionName != null)
                    {
                        if (!regionName.Contains("@"))
                        {
                            List<GridRegion> regions = m_GridService.GetRegionsByName(scopeID, regionName, 1);
                            if ((regions == null) || (regions != null && regions.Count == 0))
                            {
                                m_log.InfoFormat("[LLLOGIN SERVICE]: Got Custom Login URI {0}, can't locate region {1}. Trying defaults.", startLocation, regionName);
                                regions = m_GridService.GetDefaultRegions(scopeID);
                                if (regions != null && regions.Count > 0)
                                {
                                    where = "safe";
                                    return regions[0];
                                }
                                else
                                {
                                    m_log.Info("[LLOGIN SERVICE]: Last Region Not Found Attempting to find random region");
                                    region = FindAlternativeRegion(scopeID);
                                    if (region != null)
                                    {
                                        where = "safe";
                                        return region;
                                    }
                                    else
                                    {
                                        m_log.InfoFormat("[LLLOGIN SERVICE]: Got Custom Login URI {0}, Grid does not provide default regions and no alternative found.", startLocation);
                                        return null;
                                    }
                                }
                            }

                            //find a exact match
                            foreach(GridRegion r in regions)
                            {
                                if(string.Equals(regionName, r.RegionName, StringComparison.InvariantCultureIgnoreCase))
                                    return r;
                            }
                            // else, whatever
                            return regions[0];
                        }
                        else
                        {
                            if (m_UserAgentService == null)
                            {
                                m_log.WarnFormat("[LLLOGIN SERVICE]: This llogin service is not running a user agent service, as such it can't lauch agents at foreign grids");
                                return null;
                            }
                            string[] parts = regionName.Split(new char[] { '@' });
                            if (parts.Length < 2)
                            {
                                m_log.InfoFormat("[LLLOGIN SERVICE]: Got Custom Login URI {0}, can't locate region {1}", startLocation, regionName);
                                return null;
                            }
                            // Valid specification of a remote grid

                            regionName = parts[0];
                            string domainLocator = parts[1];
                            parts = domainLocator.Split(new char[] {':'});
                            string domainName = parts[0];
                            uint regionport = 0;
                            if (parts.Length > 1)
                                UInt32.TryParse(parts[1], out regionport);

                            region = FindForeignRegion(domainName, regionport, regionName, account, out gatekeeper);
                            return region;
                        }
                    }
                    else
                    {
                        List<GridRegion> defaults = m_GridService.GetDefaultRegions(scopeID);
                        if (defaults != null && defaults.Count > 0)
                        {
                            where = "safe";
                            return defaults[0];
                        }
                        else
                            return null;
                    }
                }
                //response.LookAt = "[r0,r1,r0]";
                //// can be: last, home, safe, url
                //response.StartLocation = "url";
            }

        }

        private GridRegion FindAlternativeRegion(UUID scopeID)
        {
            List<GridRegion> hyperlinks = null;
            List<GridRegion> regions = m_GridService.GetFallbackRegions(scopeID, (int)Util.RegionToWorldLoc(1000), (int)Util.RegionToWorldLoc(1000));
            if (regions != null && regions.Count > 0)
            {
                hyperlinks = m_GridService.GetHyperlinks(scopeID);
                IEnumerable<GridRegion> availableRegions = regions.Except(hyperlinks);
                if (availableRegions.Count() > 0)
                    return availableRegions.ElementAt(0);
            }
            // No fallbacks, try to find an arbitrary region that is not a hyperlink
            // maxNumber is fixed for now; maybe use some search pattern with increasing maxSize here?
            regions = m_GridService.GetRegionsByName(scopeID, "", 10);
            if (regions != null && regions.Count > 0)
            {
                if (hyperlinks == null)
                    hyperlinks = m_GridService.GetHyperlinks(scopeID);
                IEnumerable<GridRegion> availableRegions = regions.Except(hyperlinks);
                if (availableRegions.Count() > 0)
                    return availableRegions.ElementAt(0);
            }
            return null;
        }

        private GridRegion FindForeignRegion(string domainName, uint port, string regionName, UserAccount account, out GridRegion gatekeeper)
        {
            m_log.Debug("[LLLOGIN SERVICE]: attempting to findforeignregion " + domainName + ":" + port.ToString() + ":" + regionName);
            gatekeeper = new GridRegion();
            gatekeeper.ExternalHostName = domainName;
            gatekeeper.HttpPort = port;
            gatekeeper.RegionName = regionName;
            gatekeeper.InternalEndPoint = new IPEndPoint(IPAddress.Parse("0.0.0.0"), 0);

            UUID regionID;
            ulong handle;
            string imageURL = string.Empty, reason = string.Empty;
            string message;
            int sizeX = (int)Constants.RegionSize;
            int sizeY = (int)Constants.RegionSize;

            if (m_GatekeeperConnector.LinkRegion(gatekeeper, out regionID, out handle, out domainName, out imageURL, out reason, out sizeX, out sizeY))
            {
                string homeURI = null;
                if (account.ServiceURLs != null && account.ServiceURLs.ContainsKey("HomeURI"))
                    homeURI = (string)account.ServiceURLs["HomeURI"];

                GridRegion destination = m_GatekeeperConnector.GetHyperlinkRegion(gatekeeper, regionID, account.PrincipalID, homeURI, out message);
                return destination;
            }

            return null;
        }

        private string hostName = string.Empty;
        private int port = 0;

        private void SetHostAndPort(string url)
        {
            try
            {
                Uri uri = new Uri(url);
                hostName = uri.Host;
                port = uri.Port;
            }
            catch
            {
                m_log.WarnFormat("[LLLogin SERVICE]: Unable to parse GatekeeperURL {0}", url);
            }
        }

        protected AgentCircuitData LaunchAgentAtGrid(GridRegion gatekeeper, GridRegion destination, UserAccount account, AvatarAppearance avatar,
            UUID session, UUID secureSession, Vector3 position, string currentWhere, string viewer, string channel, string mac, string id0,
            IPEndPoint clientIP, TeleportFlags flags, out string where, out string reason, out GridRegion dest)
        {
            where = currentWhere;
            ISimulationService simConnector = null;
            reason = string.Empty;
            uint circuitCode = 0;
            AgentCircuitData aCircuit = null;
            dest = null;

            bool success = false;

            if (m_UserAgentService == null)
            {
                // HG standalones have both a localSimulatonDll and a remoteSimulationDll
                // non-HG standalones have just a localSimulationDll
                // independent login servers have just a remoteSimulationDll
                if (m_LocalSimulationService != null)
                    simConnector = m_LocalSimulationService;
                else if (m_RemoteSimulationService != null)
                    simConnector = m_RemoteSimulationService;

                if(simConnector == null)
                    return null;

                circuitCode = (uint)Util.RandomClass.Next(); ;
                aCircuit = MakeAgent(destination, account, avatar, session, secureSession, circuitCode, position,
                    clientIP.Address.ToString(), viewer, channel, mac, id0);

                success = LaunchAgentDirectly(simConnector, destination, aCircuit, flags, out reason);
                if (!success && m_GridService != null)
                {
                    // Try the fallback regions
                    List<GridRegion> fallbacks = m_GridService.GetFallbackRegions(account.ScopeID, destination.RegionLocX, destination.RegionLocY);
                    if (fallbacks != null)
                    {
                        foreach (GridRegion r in fallbacks)
                        {
                            success = LaunchAgentDirectly(simConnector, r, aCircuit, flags | TeleportFlags.ViaRegionID, out reason);
                            if (success)
                            {
                                where = "safe";
                                destination = r;
                                break;
                            }
                        }
                    }
                }
            }

            else
            {
                if (gatekeeper == null) // login to local grid
                {
                    if (hostName == string.Empty)
                        SetHostAndPort(m_GatekeeperURL);

                    gatekeeper = new GridRegion(destination);
                    gatekeeper.ExternalHostName = hostName;
                    gatekeeper.HttpPort = (uint)port;
                    gatekeeper.ServerURI = m_GatekeeperURL;
                }
                circuitCode = (uint)Util.RandomClass.Next(); ;
                aCircuit = MakeAgent(destination, account, avatar, session, secureSession, circuitCode, position,
                        clientIP.Address.ToString(), viewer, channel, mac, id0);

                aCircuit.teleportFlags |= (uint)flags;
                success = LaunchAgentIndirectly(gatekeeper, destination, aCircuit, clientIP, out reason);
                if (!success && m_GridService != null)
                {
                    // Try the fallback regions
                    List<GridRegion> fallbacks = m_GridService.GetFallbackRegions(account.ScopeID, destination.RegionLocX, destination.RegionLocY);
                    if (fallbacks != null)
                    {
                        foreach (GridRegion r in fallbacks)
                        {
                            success = LaunchAgentIndirectly(gatekeeper, r, aCircuit, clientIP, out reason);
                            if (success)
                            {
                                where = "safe";
                                destination = r;
                                break;
                            }
                        }
                    }
                }
            }
            dest = destination;
            if (success)
                return aCircuit;
            else
                return null;
        }

        private AgentCircuitData MakeAgent(GridRegion region, UserAccount account,
            AvatarAppearance avatar, UUID session, UUID secureSession, uint circuit, Vector3 position,
            string ipaddress, string viewer, string channel, string mac, string id0)
        {
            AgentCircuitData aCircuit = new AgentCircuitData();

            aCircuit.AgentID = account.PrincipalID;
            if (avatar != null)
                aCircuit.Appearance = new AvatarAppearance(avatar);
            else
                aCircuit.Appearance = new AvatarAppearance();

            //aCircuit.BaseFolder = irrelevant
            aCircuit.CapsPath = CapsUtil.GetRandomCapsObjectPath();
            aCircuit.child = false; // the first login agent is root
            aCircuit.ChildrenCapSeeds = new Dictionary<ulong, string>();
            aCircuit.circuitcode = circuit;
            aCircuit.firstname = account.FirstName;
            //aCircuit.InventoryFolder = irrelevant
            aCircuit.lastname = account.LastName;
            aCircuit.displayname = account.DisplayName;
            aCircuit.SecureSessionID = secureSession;
            aCircuit.SessionID = session;
            aCircuit.startpos = position;
            aCircuit.IPAddress = ipaddress;
            aCircuit.Viewer = viewer;
            aCircuit.Channel = channel;
            aCircuit.Mac = mac;
            aCircuit.Id0 = id0;
            SetServiceURLs(aCircuit, account);

            return aCircuit;
        }

        private void SetServiceURLs(AgentCircuitData aCircuit, UserAccount account)
        {
            aCircuit.ServiceURLs = new Dictionary<string, object>();
            if (account.ServiceURLs == null)
                return;

            // Old style: get the service keys from the DB
            foreach (KeyValuePair<string, object> kvp in account.ServiceURLs)
            {
                if (kvp.Value != null)
                {
                    aCircuit.ServiceURLs[kvp.Key] = kvp.Value;

                    if (!aCircuit.ServiceURLs[kvp.Key].ToString().EndsWith("/"))
                        aCircuit.ServiceURLs[kvp.Key] = aCircuit.ServiceURLs[kvp.Key] + "/";
                }
            }

            // New style: service keys  start with SRV_; override the previous
            string[] keys = m_LoginServerConfig.GetKeys();

            if (keys.Length > 0)
            {
                bool newUrls = false;
                IEnumerable<string> serviceKeys = keys.Where(value => value.StartsWith("SRV_"));
                foreach (string serviceKey in serviceKeys)
                {
                    string keyName = serviceKey.Replace("SRV_", "");
                    string keyValue = m_LoginServerConfig.GetString(serviceKey, string.Empty);
                    if (!keyValue.EndsWith("/"))
                        keyValue = keyValue + "/";

                    if (!account.ServiceURLs.ContainsKey(keyName) || (account.ServiceURLs.ContainsKey(keyName) && (string)account.ServiceURLs[keyName] != keyValue))
                    {
                        account.ServiceURLs[keyName] = keyValue;
                        newUrls = true;
                    }
                    aCircuit.ServiceURLs[keyName] = keyValue;

//                    m_log.DebugFormat("[LLLOGIN SERVICE]: found new key {0} {1}", keyName, aCircuit.ServiceURLs[keyName]);
                }

                if (!account.ServiceURLs.ContainsKey("GatekeeperURI") && !string.IsNullOrEmpty(m_GatekeeperURL))
                {
                    m_log.DebugFormat("[LLLOGIN SERVICE]: adding gatekeeper uri {0}", m_GatekeeperURL);
                    account.ServiceURLs["GatekeeperURI"] = m_GatekeeperURL;
                    newUrls = true;
                }

                // The grid operator decided to override the defaults in the
                // [LoginService] configuration. Let's store the correct ones.
                if (newUrls)
                    m_UserAccountService.StoreUserAccount(account);
            }

        }

        private bool LaunchAgentDirectly(ISimulationService simConnector, GridRegion region, AgentCircuitData aCircuit, TeleportFlags flags, out string reason)
        {
            EntityTransferContext ctx = new EntityTransferContext();

            if (!simConnector.QueryAccess(
                    region, aCircuit.AgentID, null, true, aCircuit.startpos, new List<UUID>(), ctx, out reason))
                return false;

            return simConnector.CreateAgent(null, region, aCircuit, (uint)flags, ctx, out reason);
        }

        private bool LaunchAgentIndirectly(GridRegion gatekeeper, GridRegion destination, AgentCircuitData aCircuit, IPEndPoint clientIP, out string reason)
        {
            m_log.Debug("[LLOGIN SERVICE]: Launching agent at " + destination.RegionName);

            if (m_UserAgentService.LoginAgentToGrid(null, aCircuit, gatekeeper, destination, true, out reason))
                return true;
            return false;
        }

        #region Console Commands
        private void RegisterCommands()
        {
            //MainConsole.Instance.Commands.AddCommand
            MainConsole.Instance.Commands.AddCommand("Users", false, "login level",
                    "login level <level>",
                    "Set the minimum user level to log in", HandleLoginCommand);

            MainConsole.Instance.Commands.AddCommand("Users", false, "login reset",
                    "login reset",
                    "Reset the login level to allow all users",
                    HandleLoginCommand);

            MainConsole.Instance.Commands.AddCommand("Users", false, "login text",
                    "login text <text>",
                    "Set the text users will see on login", HandleLoginCommand);

        }

        private void HandleLoginCommand(string module, string[] cmd)
        {
            string subcommand = cmd[1];

            switch (subcommand)
            {
                case "level":
                    // Set the minimum level to allow login
                    // Useful to allow grid update without worrying about users.
                    // or fixing critical issues
                    //
                    if (cmd.Length > 2)
                    {
                        if (Int32.TryParse(cmd[2], out m_MinLoginLevel))
                            MainConsole.Instance.Output("Set minimum login level to {0}", m_MinLoginLevel);
                        else
                            MainConsole.Instance.Output("ERROR: {0} is not a valid login level",  cmd[2]);
                    }
                    break;

                case "reset":
                    m_MinLoginLevel = m_LoginServerConfig.GetInt("MinLoginLevel", 0);
                    MainConsole.Instance.Output("Reset min login level to {0}", m_MinLoginLevel);
                    break;

                case "text":
                    if (cmd.Length > 2)
                    {
                        m_WelcomeMessage = cmd[2];
                        MainConsole.Instance.Output("Login welcome message set to '{0}'", m_WelcomeMessage);
                    }
                    break;
            }
        }

        private bool SendAgentGodKillToRegion(UUID scopeID, UUID agentID , GridUserInfo guinfo)
        {
            UUID regionID = guinfo.LastRegionID;
            GridRegion regInfo = m_GridService.GetRegionByUUID(scopeID, regionID);
            if(regInfo == null)
                return false;

            string regURL = regInfo.ServerURI;
            if(String.IsNullOrEmpty(regURL))
                return false;
            
            UUID guuid = new UUID("6571e388-6218-4574-87db-f9379718315e");

            GridInstantMessage msg = new GridInstantMessage();
            msg.imSessionID = UUID.Zero.Guid;
            msg.fromAgentID = guuid.Guid;
            msg.toAgentID = agentID.Guid;
            msg.timestamp = (uint)Util.UnixTimeSinceEpoch();
            msg.fromAgentName = "GRID";
            msg.message = string.Format("New login detected");
            msg.dialog = 250; // God kick
            msg.fromGroup = false;
            msg.offline = (byte)0;
            msg.ParentEstateID = 0;
            msg.Position = Vector3.Zero;
            msg.RegionID = scopeID.Guid;
            msg.binaryBucket = new byte[1] {0};
            InstantMessageServiceConnector.SendInstantMessage(regURL,msg);

            m_GridUserService.LoggedOut(agentID.ToString(),
                UUID.Zero, guinfo.LastRegionID, guinfo.LastPosition, guinfo.LastLookAt);

            return true;
        }
    }

    #endregion
}
