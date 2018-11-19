// (c) Gijs Sickenga, 2018 //

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

/// <summary>
/// Handles connecting to Twitch and communication between Twitch chat and the game.
/// Keeps a list of chat users and their data while Twitch integration is active.
/// </summary>
public class TwitchInterfacer : Singleton<TwitchInterfacer>
{
    #region Fields
    #region Server connection
    private TcpClient socket = new TcpClient();
    private NetworkStream networkStream;
    private StreamReader input;
    private StreamWriter output;
    private bool retryingServerConnection = false;
    private int consecutiveServerConnectionAttempts = 0;
    private Stopwatch serverConnectionTimer = new Stopwatch();
    // Connection attempt interval in ms.
    private const int RETRY_CONNECTION_INTERVAL = 1000;
    private const int MAX_SERVER_CONNECTION_ATTEMPTS = 10;
    #endregion

    #region IRC validation
    private bool waitingOnIrcResponse = false;
    private int consecutiveIrcLoginAttempts = 0;
    private bool ircLoggedIn = false;
    private Stopwatch ircLoginTimer = new Stopwatch();
    // Length of time (in ms) before we acknowledge the IRC login attempt failed.
    private const int IRC_LOGIN_TIMEOUT_THRESHOLD = 3500;
    private const int MAX_IRC_LOGIN_ATTEMPTS = 5;
    #endregion

    #region Channel join
    private bool waitingOnChannelJoinResponse = false;
    private int consecutiveChannelJoinAttempts = 0;
    private bool channelJoined = false;
    private Stopwatch channelJoinTimer = new Stopwatch();
    // Length of time (in ms) before we acknowledge the channel JOIN attempt failed.
    private const int JOIN_CHANNEL_TIMEOUT_THRESHOLD = 3500;
    private const int MAX_CHANNEL_JOIN_ATTEMPTS = 5;

    private string currentChannel = TwitchData.NICK_NAME;
    #endregion

    #region Sending messages
    // Queue of commands/messages for the bot to send to chat when it is able to.
    private Queue<string> commandQueue = new Queue<string>();
    // Timer used to give messages sent by the bot a minimum delay (to prevent getting timed out by Twitch).
    private Stopwatch sendCommandTimer = new Stopwatch();
    #endregion

    #region Receiving messages
    // Saves messages read from chat until they are read.
    private Queue<string> buffer = new Queue<string>();
    // List of all users who wrote a command in chat while the bot was active. Stores vote numbers, score etc.
    private List<TwitchChatUser> users = new List<TwitchChatUser>();
    public ReadOnlyCollection<TwitchChatUser> Users
    {
        get
        {
            return users.AsReadOnly();
        }
    }
    #endregion

    #region Command strings
    // Command signifiers.
    private const string ACTIVATE_PREFIX = "";
    #endregion

    #region Public properties
    /// <summary>
    /// Name of the currently joined channel.
    /// </summary>
    public string CurrentChannel
    {
        get
        {
            return currentChannel;
        }
        set
        {
            currentChannel = value.ToLower();
        }
    }
    #endregion
    #endregion

    #region Initialize & Resetting
    private void Initialize(Scene sceneA, Scene sceneB)
    {
        InteractionManager = FindObjectOfType<InteractionManager>();
    }

    public void ResetUsers()
    {
        users.Clear();
    }
    #endregion

    #region Enabling & Disabling
    private void OnEnable()
    {
        SceneManager.activeSceneChanged += Initialize;
    }
    
    /// <summary>
    /// Make sure to close connection and network stream for proper cleanup when disabling this script.
    /// </summary>
    private void OnDisable()
    {
        Disable();
    }

    private void Disable()
    {
        SceneManager.activeSceneChanged -= Initialize;

        if (socket != null)
        {
            // Can only leave channel if the server connection is established.
            if (socket.Connected)
            {
                // Can only leave channel when validated for IRC functionality, otherwise message will be dropped.
                if (ircLoggedIn)
                {
                    LeaveChannel(currentChannel);
                }
                else
                {
                    Debug.Log("Cannot leave channel, as IRC validation is not established and the message would be dropped.");
                }
            }
            else
            {
                Debug.Log("Cannot leave channel, as we're not connected to the server.");
            }
        }
        else
        {
            Debug.Log("Cannot release server connection socket to GC, as it's not initialized.");
        }

        // Make sure network stream is initialized, doens't happen if a server connection was never established.
        if (networkStream != null)
        {
            Debug.Log("Closing network stream.");
            networkStream.Close();
        }
        else
        {
            Debug.Log("Cannot close network stream, as there isn't one currently open.");
        }

        Debug.Log("Closing server connection and releasing socket to GC.");
        socket.Close();
    }
    #endregion

    #region Update()
    /// <summary>
    /// All communication between Twitch and the game is handled in here.
    /// </summary>
    private void Update()
    {
        #region Connecting
        // Are we connected to the IRC server? If not: return.
        if (!socket.Connected)
        {
            // Did we fail a connection attempt recently?
            if (!retryingServerConnection)
            {
                // Have we exceeded the max number of attempts?
                if (consecutiveServerConnectionAttempts < MAX_SERVER_CONNECTION_ATTEMPTS)
                {
                    // Attempt to connect to server.
                    if (OpenServerConnection())
                    {
                        // Server connection succeeded.
                        consecutiveServerConnectionAttempts = 0;
                        serverConnectionTimer.Stop();
                        // Now continue with rest of Update().
                    }
                    else
                    {
                        // Server connection failed.
                        // Wait a bit before attempting again.
                        Debug.Log("Waiting " + RETRY_CONNECTION_INTERVAL / 1000 + " seconds before retrying server connection...");
                        retryingServerConnection = true;
                        consecutiveServerConnectionAttempts++;
                        serverConnectionTimer.Reset();
                        serverConnectionTimer.Start();
                        return;
                    }
                }
                else
                {
                    Debug.LogError("Attempted to connect to server " + MAX_SERVER_CONNECTION_ATTEMPTS + " times in a row and failed every time. Disabling Twitch integration.");
                    // Disable Twitch integration because it failed to fully initialize.
                    gameObject.SetActive(false);
                    return;
                }
            }
            else // Connection attempt failed recently, waiting for a bit before trying again.
            {
                if (serverConnectionTimer.ElapsedMilliseconds >= RETRY_CONNECTION_INTERVAL)
                {
                    retryingServerConnection = false;
                    serverConnectionTimer.Stop();
                }
                return;
            }
        }

        // Are we validated for IRC functionality? If not: return.
        if (!ircLoggedIn)
        {
            // Are we waiting on a response from the server?
            if (!waitingOnIrcResponse)
            {
                // Have we exceeded the max number of attempts?
                if (consecutiveIrcLoginAttempts < MAX_IRC_LOGIN_ATTEMPTS)
                {
                    SendIrcLoginRequest();
                }
                else
                {
                    Debug.Log("Attempted to login to IRC functionality " + MAX_IRC_LOGIN_ATTEMPTS + " times in a row and failed every time. Disabling Twitch integration.");
                    // Disable Twitch integration because it failed to fully initialize.
                    gameObject.SetActive(false);
                }
                return;
            }
            else // Login request sent, waiting on response.
            {
                // Make sure we haven't exceeded the max waiting time for the current request.
                if (ircLoginTimer.ElapsedMilliseconds > IRC_LOGIN_TIMEOUT_THRESHOLD)
                {
                    // Timed out: something went wrong when sending/retrieving login messages.
                    ircLoginTimer.Stop();
                    Debug.Log("IRC login failed: timeout on login request (something went wrong in sending/retrieving login messages).");
                    waitingOnIrcResponse = false;
                    return;
                }

                // Check for the login reply from the server.
                if (ReadAllFromStream())
                {
                    for (int i = buffer.Count; i > 0; i--)
                    {
                        string rawMessage = buffer.Dequeue();
                        Debug.Log("[TWITCH]: " + rawMessage);

                        // Successful login reply.
                        if (rawMessage.Split(' ')[1] == "001")
                        {
                            Debug.Log("IRC login successful.");

                            ircLoggedIn = true;
                            waitingOnIrcResponse = false;
                            consecutiveIrcLoginAttempts = 0;
                            ircLoginTimer.Stop();
                            break;
                        }
                        else // Still waiting on login confirmation.
                        {
                            continue;
                        }
                    }
                }
                else // No message received.
                {
                    return;
                }
            }
        }

        // Have we joined a channel? If not: return.
        if (!channelJoined)
        {
            // Are we waiting on a response from the server?
            if (!waitingOnChannelJoinResponse)
            {
                // Have we exceeded the max number of attempts?
                if (consecutiveChannelJoinAttempts < MAX_CHANNEL_JOIN_ATTEMPTS)
                {
                    JoinChannel(currentChannel);
                }
                else
                {
                    Debug.Log("Attempted to join a channel " + MAX_CHANNEL_JOIN_ATTEMPTS + " times in a row and failed every time. Disabling Twitch integration.");
                    // Disable Twitch integration because it failed to fully initialize.
                    gameObject.SetActive(false);
                }
                return;
            }
            else // Join request sent, waiting on response.
            {
                // Make sure we haven't exceeded the max waiting time for the current request.
                if (channelJoinTimer.ElapsedMilliseconds > JOIN_CHANNEL_TIMEOUT_THRESHOLD)
                {
                    // Channel doesn't exist or something went wrong in sending or retrieving the attempt.
                    channelJoinTimer.Stop();
                    Debug.Log("Channel join failed: timeout on channel join (channel non-existant or something went wrong in sending/retrieving join messages).");
                    waitingOnChannelJoinResponse = false;
                    return;
                }

                // Check for the join reply from the server.
                if (ReadAllFromStream())
                {
                    for (int i = buffer.Count; i > 0; i--)
                    {
                        string rawMessage = buffer.Dequeue();
                        Debug.Log("[TWITCH]: " + rawMessage);

                        // Successful join reply.
                        if (rawMessage == ":" + TwitchData.NICK_NAME + "!" + TwitchData.NICK_NAME + "@" + TwitchData.NICK_NAME + ".tmi.twitch.tv JOIN #" + currentChannel)
                        {
                            Debug.Log("Channel join succeeded.");

                            channelJoined = true;
                            waitingOnChannelJoinResponse = false;
                            consecutiveChannelJoinAttempts = 0;
                            channelJoinTimer.Stop();

                            sendCommandTimer.Reset();
                            sendCommandTimer.Start();

                            // Undo any mod command garbage.
                            EnqueueMessage("/slowoff");
                            EnqueueMessage("/followersoff");
                            EnqueueMessage("/subscribersoff");
                            EnqueueMessage("/r9kbetaoff");
                            EnqueueMessage("/emoteonlyoff");
                            EnqueueMessage("/clear");

                            // Notify users the bot is active.
                            EnqueueMessage("/me The bot is now active.");
                            break;
                        }
                        else if (rawMessage.Contains("msg_channel_suspended"))
                        {
                            Debug.Log("Channel join failed: channel suspended or deleted.");
                            // Disable Twitch integration because it failed to fully initialize.
                            gameObject.SetActive(false);
                            return;
                        }
                        else // Still waiting on join confirmation.
                        {
                            continue;
                        }
                    }
                }
                else // No message received.
                {
                    return;
                }
            }
        }
        #endregion

        #region Reading messages
        // Connected to server, logged into IRC and joined channel: check for messages from the server.
        if (ReadAllFromStream())
        {
            for (int i = buffer.Count; i > 0; i--)
            {
                string rawMessage = buffer.Dequeue();
                Debug.Log("[TWITCH]: " + rawMessage);

                // Check if a chat message was sent.
                if (rawMessage.Contains("PRIVMSG #"))
                {
                    ProcessMessage(rawMessage);
                }
                else if (rawMessage.StartsWith("PING"))
                {
                    // Respond with PONG reply to PING messages to prevent being disconnected prematurely.
                    commandQueue.Enqueue(rawMessage.Replace("PING", "PONG"));
                }
            }
        }
        #endregion

        #region Send enqueued messages when possible
        // Check if enough time has elapsed to send the next command.
        if (sendCommandTimer.ElapsedMilliseconds > TwitchData.SEND_COMMAND_INTERVAL)
        {
            // Check if there are messages enqueued.
            if (commandQueue.Count > 0)
            {
                SendCommand(commandQueue.Dequeue());
            }
        }
        #endregion
    }
    #endregion

    #region Methods
    #region Server connection
    /// <summary>
    /// Attempts to open a connection to the Twitch IRC server and returns whether a connection was made.
    /// </summary>
    private bool OpenServerConnection()
    {
        try
        {
            Debug.Log("Connecting...");
            socket.Connect(TwitchData.SERVER, TwitchData.PORT);
        }
        catch (Exception e)
        {
            Debug.LogWarning("Exception thrown while trying to connect to server: " + e.ToString());
        }

        if (!socket.Connected)
        {
            Debug.LogWarning("Failed to connect to server.");
            return false;
        }

        Debug.Log("Connection attempt successful.");
        networkStream = socket.GetStream();
        input = new StreamReader(networkStream);
        output = new StreamWriter(networkStream);
        return true;
    }
    #endregion

    #region IRC validation
    /// <summary>
    /// Attempt to get validated for IRC functionality.
    /// Required before sending and receiving messages is possible.
    /// </summary>
    private void SendIrcLoginRequest()
    {
        waitingOnIrcResponse = true;
        consecutiveIrcLoginAttempts++;
        ircLoginTimer.Reset();
        ircLoginTimer.Start();

        // Login to Twitch IRC server by sending validation token and nickname.
        Debug.Log("Establishing IRC validation...");
        Debug.Log("[LOCAL]: PASS [hidden]");
        output.WriteLine("PASS oauth:" + TwitchData.TOKEN);
        Debug.Log("[LOCAL]: NICK " + TwitchData.NICK_NAME);
        output.WriteLine("NICK " + TwitchData.NICK_NAME);
        try
        {
            output.Flush();
        }
        catch (Exception e)
        {
            Debug.LogWarning(e.Message);
        }
    }
    #endregion

    #region Channel joining, leaving & swapping
    /// <summary>
    /// Attempt to join a chat channel.
    /// </summary>
    private void JoinChannel(string channelName)
    {
        waitingOnChannelJoinResponse = true;
        consecutiveChannelJoinAttempts++;
        channelJoinTimer.Reset();
        channelJoinTimer.Start();

        Debug.Log("Sending channel join request...");
        Debug.Log("[LOCAL]: JOIN #" + channelName);
        output.WriteLine("JOIN #" + channelName);
        output.Flush();
    }

    /// <summary>
    /// Attempt to leave a chat channel.
    /// </summary>
    private void LeaveChannel(string channelName)
    {
        channelJoined = false;

        Debug.Log("[LOCAL]: PRIVMSG #" + channelName + " :" + "The bot is no longer active.");
        output.WriteLine("PRIVMSG #" + channelName + " :" + "The bot is no longer active.");

        Debug.Log("Sending channel leave request...");
        Debug.Log("[LOCAL]: PART #" + channelName);
        output.WriteLine("PART #" + channelName);
        output.Flush();
    }

    /// <summary>
    /// Leave the currently joined channel and join the specified new one. Returns whether channel swapping messages were successfully sent.
    /// </summary>
    public bool SwapChannels(string newChannel)
    {
        if (channelJoined)
        {
            LeaveChannel(currentChannel);

            users.Clear();
            CurrentChannel = newChannel;

            JoinChannel(newChannel);
            return true;
        }
        return false;
    }
    #endregion

    #region Reading received messages from Twitch
    /// <summary>
    /// Checks whether there is data available to be read from the network stream, and retrieves the first message if there is.
    /// Returns whether data was read from the network stream.
    /// Use this method to process messages one at a time, so messages sent in bulk are distributed over time (1 per Update call) rather than all handled at once.
    /// </summary>
    private bool ReadFirstFromStream(out string message)
    {
        try
        {
            if (networkStream.DataAvailable)
            {
                message = input.ReadLine();
                return true;
            }
            else
            {
                message = string.Empty;
                return false;
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning("Couldn't read message from networkStream in TwitchInterfacer.ReadFirstFromStream.\n" + e.Message + "\n" + e.StackTrace);

            message = string.Empty;
            return false;
        }
    }

    /// <summary>
    /// Checks whether there is data available to be read from the network stream, and retrieves a list of all remaining messages if there is.
    /// Returns whether data was read from the network stream.
    /// Use this method to process all remaining messages at once.
    /// </summary>
    private bool ReadAllFromStream()
    {
        try
        {
            if (networkStream.DataAvailable)
            {
                while (networkStream.DataAvailable)
                {
                    buffer.Enqueue(input.ReadLine());
                }
                return true;
            }
            else
            {
                return false;
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning("Couldn't read message from networkStream in TwitchInterfacer.ReadAllFromStream.\n" + e.Message + "\n" + e.StackTrace);

            return false;
        }
    }
    #endregion

    #region Sending messages to Twitch
    /// <summary>
    /// Enqueues a basic chat message on the commandqueue.
    /// </summary>
    public void EnqueueMessage(string message)
    {
        // /me is used to color messages the same as the bot channel's chosen chat color.
        // Makes messages from the bot channel easier to distinguish from the rest of chat.
        commandQueue.Enqueue("PRIVMSG #" + currentChannel + " :" + message);
    }

    /// <summary>
    /// Enqueues a Twitch IRC command on the commandqueue. For a list of commands, check Twitch's own API.
    /// </summary>
    private void EnqueueCommand(string command)
    {
        commandQueue.Enqueue(command);
    }

    /// <summary>
    /// Writes the first enqueued command to the network stream and flushes it to the server.
    /// </summary>
    private void SendCommand(string command)
    {
        Debug.Log("[LOCAL]: " + command);
        output.WriteLine(command);
        output.Flush();

        sendCommandTimer.Reset();
        sendCommandTimer.Start();
    }
    #endregion

    #region Extracting Twitch message parts
    /// <summary>
    /// Strips a received IRC chat message of its tags and returns only the user that sent the message.
    /// </summary>
    private string ExtractUsername(string rawMessage)
    {
        return rawMessage.Substring(1, rawMessage.IndexOf('!') - 1);
    }

    /// <summary>
    /// Strips a received IRC chat message of its tags and returns only the message.
    /// </summary>
    private string ExtractMessage(string rawMessage)
    {
        return rawMessage.Substring(rawMessage.IndexOf("PRIVMSG #") + currentChannel.Length + 11);
    }
    #endregion

    #region Getting user values
    /// <summary>
    /// Gets the stored user associated with a name in chat.
    /// </summary>
    private TwitchChatUser GetUser(string userName)
    {
        foreach (TwitchChatUser user in users)
        {
            if (user.Name == userName)
            {
                return user;
            }
        }

        return null;
    }
    #endregion

    #region Command processing
    /// <summary>
    /// Determines the type of command that was sent by a user in chat.
    /// If the message wasn't a command, TwitchData.CommandType.Invalid is returned.
    /// </summary>
    private TwitchData.CommandType DetermineCommandType(string message)
    {
        string commandString = message.Split(' ')[0];
        int id = 0;
        
        switch (int.TryParse(message, out id))
        {
            case true:
                return TwitchData.CommandType.Activate;

            default:
                return TwitchData.CommandType.Invalid;
        }
    }

    /// <summary>
    /// Process a message based on the type of command it contains.
    /// </summary>
    private void ProcessMessage(string rawMessage)
    {
        /*
        // Check what kind of command was sent.
        TwitchData.CommandType commandType = DetermineCommandType(message);

        // Ignore invalid commands.
        if (commandType == TwitchData.CommandType.Invalid)
            return;
        */
        // Process user.
        string userName = ExtractUsername(rawMessage);
        TwitchChatUser user = GetUser(userName);

        // If user is not registered in list of users, save as new user.
        if (user == null)
        {
            if (RoundManager.Instance.CurrentState == RoundManager.RoundState.PreJoin ||
                RoundManager.Instance.CurrentState == RoundManager.RoundState.Join)
            {
                //user = new TwitchChatUser(userName, UnityEngine.Random.ColorHSV(0, 1, 1, 1, 1, 1));
                user = new TwitchChatUser(userName, new Color(1f / 255f * 86f, 1f / 255f * 77f, 1f / 255f * 160f));
                users.Add(user);
                EnqueueMessage("/mod " + user.Name);

                JoinedPlayersUI.AddPlayer(user);
            }
            // Don't process message from non-registered users.
            return;
        } // User must have robots alive to send commands.
        else if (!user.HasRobotsAlive)
            return;

        // Process message.
        string chatMessage = ExtractMessage(rawMessage).ToLower();
        string commandString = chatMessage.Split(' ')[0];
        int id = 0;
        if (!int.TryParse(commandString, out id))
            return;

        // Valid command sent, so process it.
        if (RoundManager.Instance.CurrentState != RoundManager.RoundState.Winner)
        {
            InteractionManager.ActivateObject(id);
        }
    }
    #endregion
    #endregion
}