// FFXI Macro Manager — Job → JobAbility mapping table.
//
// Windower's job_abilities resource has no job tag, so we maintain a
// hardcoded list of "which job natively gets which JA." Source is
// BG-Wiki per-job pages; reviewed and ASSEMBLED for FFXI Macro Manager.
//
// HOW THE FILTER WORKS
//   1. Type-based fast path covers the bulk: BloodPactRage/Ward = SMN,
//      CorsairRoll/Shot = COR, Scholar = SCH, Waltz/Samba/Step/Jig/
//      Flourish* = DNC, Rune/Effusion/Ward = RUN.
//   2. The 236 generic "JobAbility" type entries are routed by the
//      explicit name -> job(s) map below.
//   3. PetCommand entries (maneuvers, pet commands) are routed
//      multi-job — Heel/Fight/etc. fire for both BST and SMN.
//
// ONLY NATIVE-job assignments are listed. Sub-job borrowed JAs are NOT
// shown when a job filter is applied — pick "(all jobs)" if you want
// to macro a subjob ability.
//
// If a name is missing from the maps below, AbilitiesFor() falls back
// to showing it under "(all jobs)" only. To add coverage, append to
// the relevant job's array in JobAbilitiesByJob below.

using System;
using System.Collections.Generic;

namespace FFXIMacroManager.Models
{
    public static class JobAbilityMap
    {
        // Job IDs from Windower res/jobs.lua.
        public const int WAR = 1,  MNK = 2,  WHM = 3,  BLM = 4,  RDM = 5,
                         THF = 6,  PLD = 7,  DRK = 8,  BST = 9,  BRD = 10,
                         RNG = 11, SAM = 12, NIN = 13, DRG = 14, SMN = 15,
                         BLU = 16, COR = 17, PUP = 18, DNC = 19, SCH = 20,
                         GEO = 21, RUN = 22;

        // Per-job native JobAbility names (the generic type="JobAbility"
        // entries — the typed ones like CorsairRoll are handled by
        // TypeOwners below and don't need to appear here).
        //
        // Ordered roughly by tier within each job so the source diff
        // stays reviewable (BG-Wiki ordering doesn't match level, that's
        // intentional — we just need the membership set, not an order).
        private static readonly Dictionary<int, string[]> JobAbilitiesByJob =
            new Dictionary<int, string[]>
        {
            { WAR, new[] {
                "Provoke","Berserk","Defender","Warcry","Aggressor",
                "Mighty Strikes","Tomahawk","Restraint","Blood Rage",
                "Retaliation","Brazen Rush","Warrior's Charge",
            }},
            { MNK, new[] {
                "Boost","Dodge","Focus","Counterstance","Chakra",
                "Hundred Fists","Mantra","Footwork","Formless Strikes",
                "Perfect Counter","Impetus","Inner Strength","Chi Blast",
            }},
            { WHM, new[] {
                "Divine Seal","Benediction","Afflatus Solace","Afflatus Misery",
                "Devotion","Asylum","Sacrosanctity","Martyr","Divine Caress",
            }},
            { BLM, new[] {
                "Elemental Seal","Manafont","Mana Wall","Subtle Sorcery",
                "Manawell",
            }},
            { RDM, new[] {
                "Convert","Chainspell","Spontaneity","Saboteur","Stymie",
                "Composure",
            }},
            { THF, new[] {
                "Steal","Flee","Hide","Sneak Attack","Trick Attack","Mug",
                "Perfect Dodge","Despoil","Conspirator","Assassin's Charge",
                "Larceny","Feint","Accomplice","Collaborator","Bully",
            }},
            { PLD, new[] {
                "Holy Circle","Shield Bash","Sentinel","Cover","Rampart",
                "Divine Emblem","Invincible","Sepulcher","Palisade",
                "Fealty","Chivalry","Intervene","Majesty",
            }},
            { DRK, new[] {
                "Last Resort","Souleater","Arcane Circle","Weapon Bash",
                "Diabolic Eye","Blood Weapon","Nether Void","Scarlet Delirium",
                "Consume Mana","Arcane Crest","Soul Enslavement","Dark Seal",
            }},
            { BST, new[] {
                "Charm","Reward","Tame","Familiar","Killer Instinct",
                "Feral Howl","Spur","Run Wild","Bestial Loyalty","Call Beast",
                "Unleash",
            }},
            { BRD, new[] {
                "Soul Voice","Pianissimo","Nightingale","Troubadour",
                "Tenuto","Marcato","Clarion Call",
            }},
            { RNG, new[] {
                "Sharpshot","Camouflage","Barrage","Scavenge","Eagle Eye Shot",
                "Bounty Shot","Decoy Shot","Stealth Shot","Velocity Shot",
                "Unlimited Shot","Shadowbind","Double Shot","Flashy Shot",
                "Hover Shot",
            }},
            { SAM, new[] {
                "Third Eye","Hasso","Seigan","Meditate","Sekkanoki",
                "Warding Circle","Meikyo Shisui","Konzen-ittai","Hamanoha",
                "Yaegasumi","Shikikoyo","Blade Bash","Hagakure","Sengikori",
            }},
            { NIN, new[] {
                "Mijin Gakure","Futae","Yonin","Innin","Sange","Issekigan",
                "Mikage",
            }},
            { DRG, new[] {
                "Ancient Circle","Jump","High Jump","Super Jump",
                "Call Wyvern","Spirit Surge","Spirit Link","Dragon Breaker",
                "Fly High","Spirit Jump","Soul Jump","Deep Breathing",
                "Spirit Bond","Angon",
            }},
            { SMN, new[] {
                "Astral Flow","Elemental Siphon","Astral Conduit","Apogee",
            }},
            { BLU, new[] {
                "Burst Affinity","Chain Affinity","Azure Lore","Diffusion",
                "Convergence","Efflux","Unbridled Learning","Unbridled Wisdom",
            }},
            { COR, new[] {
                "Phantom Roll","Random Deal","Double-Up","Fold","Snake Eye",
                "Crooked Cards","Wild Card","Cutting Cards","Triple Shot",
                "Quick Draw",
            }},
            { PUP, new[] {
                "Activate","Repair","Deactivate","Overdrive","Tactical Switch",
                "Heady Artifice","Role Reversal","Cooldown","Ventriloquy",
                "Maintenance","Deus Ex Automata",
            }},
            { DNC, new[] {
                "Trance","No Foot Rise","Saber Dance","Fan Dance",
                "Closed Position","Presto","Grand Pas","Contradance",
                // The "category" parent entries the game lists in the JA
                // menu — included so users searching for "Waltzes" find it.
                "Sambas","Steps","Waltzes","Jigs",
                "Flourishes I","Flourishes II","Flourishes III",
            }},
            { SCH, new[] {
                "Light Arts","Dark Arts","Sublimation","Tabula Rasa",
                "Enlightenment","Caper Emissarius","Modus Veritas","Libra",
                "Stratagems",
            }},
            { GEO, new[] {
                "Bolster","Life Cycle","Blaze of Glory","Dematerialize",
                "Mending Halation","Radial Arcana","Theurgic Focus",
                "Full Circle","Concentric Pulse","Ecliptic Attrition",
                "Entrust","Mana Cede","Widened Compass","Lasting Emanation",
                "Collimated Fervor","Cascade",
            }},
            { RUN, new[] {
                "Vivacious Pulse","Rune Enchantment","Embolden",
                "Elemental Sforzo","One for All","Odyllic Subterfuge",
                "Enmity Douse",
            }},
        };

        // PetCommand entries are physically usable by multiple jobs in
        // different contexts. We map each to ALL jobs that legitimately
        // issue it via /pet, so filtering shows them under BST or SMN
        // or PUP or DRG as appropriate.
        private static readonly Dictionary<string, int[]> PetCommandOwners =
            new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase)
        {
            // BST pet movement / control
            { "Fight",       new[] { BST } },
            { "Heel",        new[] { BST } },
            { "Stay",        new[] { BST } },
            { "Snarl",       new[] { BST } },
            { "Leave",       new[] { BST } },
            { "Sic",         new[] { BST } },
            { "Spur",        new[] { BST } },
            { "Run Wild",    new[] { BST } },
            { "Ready",       new[] { BST } },

            // SMN summon control + JA-as-PetCommand
            { "Assault",     new[] { SMN, BST } },
            { "Avatar's Favor", new[] { SMN } },
            { "Blood Pact: Rage", new[] { SMN } },
            { "Blood Pact: Ward", new[] { SMN } },
            { "Release",     new[] { SMN } },
            { "Retreat",     new[] { SMN } },
            { "Dismiss",     new[] { SMN } },

            // PUP automaton + maneuvers
            { "Deploy",      new[] { PUP } },
            { "Retrieve",    new[] { PUP } },
            { "Dark Maneuver",    new[] { PUP } },
            { "Earth Maneuver",   new[] { PUP } },
            { "Fire Maneuver",    new[] { PUP } },
            { "Ice Maneuver",     new[] { PUP } },
            { "Light Maneuver",   new[] { PUP } },
            { "Thunder Maneuver", new[] { PUP } },
            { "Water Maneuver",   new[] { PUP } },
            { "Wind Maneuver",    new[] { PUP } },

            // DRG wyvern breath
            { "Restoring Breath", new[] { DRG } },
            { "Smiting Breath",   new[] { DRG } },
            { "Steady Wing",      new[] { DRG } },
        };

        // Type -> owning job. Covers categories where 100% of the entries
        // belong to a single job — no per-name lookup needed.
        private static readonly Dictionary<string, int> SingleTypeOwners =
            new Dictionary<string, int>(StringComparer.Ordinal)
        {
            { "BloodPactRage", SMN },
            { "BloodPactWard", SMN },
            { "CorsairRoll",   COR },
            { "CorsairShot",   COR },
            { "Scholar",       SCH },
            { "Waltz",         DNC },
            { "Samba",         DNC },
            { "Step",          DNC },
            { "Jig",           DNC },
            { "Flourish1",     DNC },
            { "Flourish2",     DNC },
            { "Flourish3",     DNC },
            { "Rune",          RUN },
            { "Effusion",      RUN },
            { "Ward",          RUN },   // Battuta/Liement/Pflug/Valiance/Vallation
        };

        // Reverse-built once at static init: ability name -> owning job IDs.
        // (Combined view of JobAbilitiesByJob + PetCommandOwners. Type-only
        // mappings stay in SingleTypeOwners and are checked separately.)
        private static readonly Dictionary<string, HashSet<int>> _nameToJobs;

        static JobAbilityMap()
        {
            _nameToJobs = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in JobAbilitiesByJob)
            {
                foreach (var name in kv.Value)
                {
                    HashSet<int> jobs;
                    if (!_nameToJobs.TryGetValue(name, out jobs))
                    {
                        jobs = new HashSet<int>();
                        _nameToJobs[name] = jobs;
                    }
                    jobs.Add(kv.Key);
                }
            }
            foreach (var kv in PetCommandOwners)
            {
                HashSet<int> jobs;
                if (!_nameToJobs.TryGetValue(kv.Key, out jobs))
                {
                    jobs = new HashSet<int>();
                    _nameToJobs[kv.Key] = jobs;
                }
                foreach (var j in kv.Value) jobs.Add(j);
            }
        }

        // Returns true if the given (name, type) combo belongs to the
        // requested job. Pass jobId=0 to mean "any job" (always true).
        public static bool Belongs(string name, string type, int jobId)
        {
            if (jobId <= 0) return true;
            if (string.IsNullOrEmpty(name)) return false;

            // Exclude mob-only abilities from any per-job filter.
            if (type == "Monster") return false;

            // Type-based single-owner check first (cheapest).
            int owner;
            if (SingleTypeOwners.TryGetValue(type ?? "", out owner))
                return owner == jobId;

            // Name-based check for the generic JobAbility / PetCommand pile.
            HashSet<int> jobs;
            if (_nameToJobs.TryGetValue(name, out jobs))
                return jobs.Contains(jobId);

            // Unknown ability — exclude from per-job listings (will still
            // show under "(all jobs)"). Better to be conservative than to
            // pollute a job's list with unrelated stuff.
            return false;
        }
    }
}
