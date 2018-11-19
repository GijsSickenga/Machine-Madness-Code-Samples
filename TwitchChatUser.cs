// (c) Gijs Sickenga, 2018 //

using System.Collections.Generic;
using UnityEngine;

public class TwitchChatUser
{
    public TwitchChatUser(string userName, Color color)
    {
        this.userName = userName;
        this.color = color;
    }

    private string userName;
    public string Name
    {
        get
        {
            return userName;
        }
    }

    private Color color;
    public Color Color
    {
        get
        {
            return color;
        }

        set
        {
            color = value;
        }
    }

    private bool hasRobotsAlive = true;
    public bool HasRobotsAlive
    {
        get
        {
            return hasRobotsAlive;
        }
    }

    private Dictionary<GameObject, bool> robotsAlive = new Dictionary<GameObject, bool>();
    public void AssignRobots(List<GameObject> robots)
    {
        foreach (GameObject robot in robots)
        {
            robotsAlive.Add(robot, true);
        }
    }

    public void RobotHasDied(GameObject deadRobot)
    {
        robotsAlive[deadRobot] = false;
        foreach (KeyValuePair<GameObject, bool> robotIsAlive in robotsAlive)
        {
            if (robotIsAlive.Value)
            {
                return;
            }
        }
        hasRobotsAlive = false;
        RoundManager.Instance.KillUser(this);
    }
}
