namespace Content.Server.Psionics
{
    [RegisterComponent]
    public sealed class PotentialPsionicComponent : Component
    {
        [DataField("chance")]
        public float Chance = 0.045f;

        /// <summary>
        /// YORO (you only reroll once)
        /// </summary>
        public bool Rerolled = false;
    }
}