using UnityEngine;

/// <summary>
/// Each herbivore rolls a personality at spawn. Personality modifies steering weights,
/// social behaviour, infection susceptibility, and visual appearance.
/// </summary>
public enum PersonalityType
{
    Cautious,   // stays near edges, flees fast, hard to infect via proximity
    Anxious,    // very fast direction changes, keeps distance from others
    Social,     // seeks herd mates, highly vulnerable to infected herd corralling
    Bold,       // ignores fungal regions for longer, slow to flee
    Wanderer,   // large roaming radius, rarely clusters
    Timid       // freezes briefly when threatened, easy target for infected herds
}

[System.Serializable]
public class HerbivorePersonality
{
    public PersonalityType type;

    // ----- Steering modifiers -----
    [Tooltip("Multiplier on flee/avoid force")]
    public float fleeStrength;

    [Tooltip("Multiplier on social/cohesion force toward healthy herd")]
    public float socialAttraction;

    [Tooltip("How strongly this herbivore avoids fungus tiles and fungal regions")]
    public float fungusAvoidance;

    [Tooltip("How strongly this herbivore avoids infected animals")]
    public float infectedAvoidance;

    [Tooltip("How early this herbivore detects incoming acid rain and seeks shelter")]
    public float rainPerception;

    [Tooltip("How strongly infected herd's corralling force affects this animal")]
    public float corralVulnerability;

    [Tooltip("Multiplier on wander randomness")]
    public float wanderNoise;

    // ----- Infection -----
    [Tooltip("Multiplier on base infection chance per tick")]
    public float infectionSusceptibility;

    [Tooltip("Multiplier on breeding chance when conditions are valid")]
    public float breedingDrive;

    // ----- Visual -----
    public Color healthyColor;
    public float entityScale;

    // ----- Factory -----
    public static HerbivorePersonality Random()
    {
        PersonalityType t = (PersonalityType)UnityEngine.Random.Range(0, System.Enum.GetValues(typeof(PersonalityType)).Length);
        return FromType(t);
    }

    public static HerbivorePersonality FromType(PersonalityType t)
    {
        return t switch
        {
            PersonalityType.Cautious => new HerbivorePersonality
            {
                type = t,
                fleeStrength = 2.2f,
                socialAttraction = 0.6f,
                fungusAvoidance = 2.4f,
                infectedAvoidance = 2.1f,
                rainPerception = 2.4f,
                corralVulnerability = 0.4f,
                wanderNoise = 0.5f,
                infectionSusceptibility = 0.6f,
                breedingDrive = 0.8f,
                healthyColor = new Color(0.35f, 0.85f, 0.35f),
                entityScale = 0.22f
            },
            PersonalityType.Anxious => new HerbivorePersonality
            {
                type = t,
                fleeStrength = 1.8f,
                socialAttraction = 0.3f,
                fungusAvoidance = 2.0f,
                infectedAvoidance = 2.5f,
                rainPerception = 1.8f,
                corralVulnerability = 0.5f,
                wanderNoise = 2.0f,
                infectionSusceptibility = 0.8f,
                breedingDrive = 0.7f,
                healthyColor = new Color(0.9f, 0.9f, 0.25f),
                entityScale = 0.18f
            },
            PersonalityType.Social => new HerbivorePersonality
            {
                type = t,
                fleeStrength = 0.9f,
                socialAttraction = 2.5f,
                fungusAvoidance = 0.7f,
                infectedAvoidance = 0.8f,
                rainPerception = 1.0f,
                corralVulnerability = 1.8f,   // easily swept up by infected herd
                wanderNoise = 0.4f,
                infectionSusceptibility = 1.4f,
                breedingDrive = 1.5f,
                healthyColor = new Color(0.3f, 0.75f, 1.0f),
                entityScale = 0.24f
            },
            PersonalityType.Bold => new HerbivorePersonality
            {
                type = t,
                fleeStrength = 0.4f,
                socialAttraction = 0.8f,
                fungusAvoidance = 0.35f,
                infectedAvoidance = 0.5f,
                rainPerception = 0.4f,
                corralVulnerability = 0.7f,
                wanderNoise = 0.7f,
                infectionSusceptibility = 1.3f,
                breedingDrive = 1.1f,
                healthyColor = new Color(1.0f, 0.55f, 0.1f),
                entityScale = 0.28f
            },
            PersonalityType.Wanderer => new HerbivorePersonality
            {
                type = t,
                fleeStrength = 1.0f,
                socialAttraction = 0.2f,
                fungusAvoidance = 1.0f,
                infectedAvoidance = 0.7f,
                rainPerception = 0.9f,
                corralVulnerability = 0.6f,
                wanderNoise = 1.5f,
                infectionSusceptibility = 0.9f,
                breedingDrive = 0.9f,
                healthyColor = new Color(0.8f, 0.5f, 1.0f),
                entityScale = 0.20f
            },
            PersonalityType.Timid => new HerbivorePersonality
            {
                type = t,
                fleeStrength = 1.5f,
                socialAttraction = 1.2f,
                fungusAvoidance = 2.8f,
                infectedAvoidance = 2.9f,
                rainPerception = 2.9f,
                corralVulnerability = 2.5f,   // freezes, worst victim
                wanderNoise = 0.3f,
                infectionSusceptibility = 1.6f,
                breedingDrive = 0.6f,
                healthyColor = new Color(1.0f, 0.75f, 0.8f),
                entityScale = 0.19f
            },
            _ => new HerbivorePersonality
            {
                type = t,
                fleeStrength = 1f,
                socialAttraction = 1f,
                fungusAvoidance = 1f,
                infectedAvoidance = 1f,
                rainPerception = 1f,
                corralVulnerability = 1f,
                wanderNoise = 1f,
                infectionSusceptibility = 1f,
                breedingDrive = 1f,
                healthyColor = Color.green,
                entityScale = 0.22f
            }
        };
    }
}
