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

using System.Collections.Generic;
using System.Net;
using System.Reflection;
using log4net;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Servers;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Services.Interfaces;

namespace OpenSim
{
    public abstract class RegionApplicationBase : BaseOpenSimServer
    {
        private static readonly ILog m_log
            = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        protected Dictionary<EndPoint, uint> m_clientCircuits = new Dictionary<EndPoint, uint>();
        protected NetworkServersInfo m_networkServersInfo;
        protected uint m_httpServerPort;
        protected bool m_httpServerSSL;
        protected ISimulationDataService m_simulationDataService;
        protected IEstateDataService m_estateDataService;

        public SceneManager SceneManager { get; protected set; }
        public NetworkServersInfo NetServersInfo { get { return m_networkServersInfo; } }
        public ISimulationDataService SimulationDataService { get { return m_simulationDataService; } }
        public IEstateDataService EstateDataService { get { return m_estateDataService; } }

        protected abstract void Initialize();

        protected abstract Scene CreateScene(RegionInfo regionInfo, ISimulationDataService simDataService, IEstateDataService estateDataService, AgentCircuitManager circuitManager);

        protected override void StartupSpecific()
        {
            SceneManager = SceneManager.Instance;

            Initialize();

            IPAddress ipaddress = m_networkServersInfo.HttpListenerAddress;

            uint mainport = m_networkServersInfo.HttpListenerPort;
            uint mainSSLport = m_networkServersInfo.httpSSLPort;

            if (m_networkServersInfo.HttpUsesSSL && (mainport == mainSSLport))
            {
                m_log.Error("[REGION SERVER]: HTTP Server config failed.   HTTP Server and HTTPS server must be on different ports");
            }

            if(m_networkServersInfo.HttpUsesSSL)
            {
                m_httpServer = new BaseHttpServer(
                        mainSSLport, m_networkServersInfo.HttpUsesSSL,
                        m_networkServersInfo.HttpSSLCN,
                        m_networkServersInfo.HttpSSLCertPath, m_networkServersInfo.HttpSSLCNCertPass);
                m_httpServer.Start();
                MainServer.AddHttpServer(m_httpServer);
            }

            // unsecure main server
            BaseHttpServer server = new BaseHttpServer(ipaddress, mainport);
            if(!m_networkServersInfo.HttpUsesSSL)
            {
                m_httpServer = server;
                server.Start(m_networkServersInfo.HttpListenerPortMin, m_networkServersInfo.HttpListenerPortMax);
                // hack: update the config to the selected port
                m_networkServersInfo.HttpListenerPort = server.Port;
                Config.Configs["Network"].Set("http_listener_port", server.Port);
				m_httpServerPort = server.Port;
            }
            else
                server.Start();

            MainServer.AddHttpServer(server);
            MainServer.UnSecureInstance = server;

            MainServer.Instance = m_httpServer;

            // "OOB" Server
            if (m_networkServersInfo.ssl_listener)
            {
                if (!m_networkServersInfo.ssl_external)
                {
                    server = new BaseHttpServer(
                        m_networkServersInfo.https_port, m_networkServersInfo.ssl_listener,
                        m_networkServersInfo.cert_path,
                        m_networkServersInfo.cert_pass);

                    m_log.InfoFormat("[REGION SERVER]: Starting OOB HTTPS server on port {0}", server.SSLPort);
                    server.Start();
                    MainServer.AddHttpServer(server);
                }
                else
                {
                    server = new BaseHttpServer(m_networkServersInfo.https_port);

                    m_log.InfoFormat("[REGION SERVER]: Starting HTTP server on port {0} for external HTTPS", server.Port);
                    server.Start();
                    MainServer.AddHttpServer(server);
                }
            }

            base.StartupSpecific();
        }

    }
}
