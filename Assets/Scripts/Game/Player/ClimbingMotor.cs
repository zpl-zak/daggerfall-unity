using UnityEngine;
using System;
using System.Collections;
using DaggerfallConnect;

namespace DaggerfallWorkshop.Game
{

    [RequireComponent(typeof(PlayerMotor))]
    [RequireComponent(typeof(LevitateMotor))]
    [RequireComponent(typeof(CharacterController))]
    public class ClimbingMotor : MonoBehaviour
    {
        private Entity.PlayerEntity player;
        private PlayerMotor playerMotor;
        private LevitateMotor levitateMotor;
        private CharacterController controller;
        private PlayerEnterExit playerEnterExit;
        private AcrobatMotor acrobatMotor;
        private bool isClimbing = false;
        private bool isSlipping = false;
        private bool atOutsideCorner = false;
        private float climbingStartTimer = 0;
        private float climbingContinueTimer = 0;
        private bool showClimbingModeMessage = true;
        private Vector2 lastHorizontalPosition = Vector2.zero;
        private Vector3 ledgeDirection = Vector3.zero;
        private Vector3 adjacentLedgeDirection = Vector3.zero;
        private Ray myStrafeRay = new Ray();
        private Ray adjacentWallRay = new Ray();
        /// <summary>
        /// The normal that sticks diagonally out of the wall corner we're near. Equal-angled on both sides.
        /// </summary>
        private Ray cornerNormalRay = new Ray();
        // the ultimate movement direction
        private Vector3 moveDirection = Vector3.zero;
        // how long it takes before we do another skill check to see if we can continue climbing
        private const int continueClimbingSkillCheckFrequency = 15; 
        // how long it takes before we try to regain hold if slipping
        private readonly float regainHoldSkillCheckFrequency = 5; 
        // minimum percent chance to regain hold per skill check if slipping, gets closer to 100 with higher skill
        private const int regainHoldMinChance = 20;
        // minimum percent chance to continue climbing per skill check, gets closer to 100 with higher skill
        private const int continueClimbMinChance = 70;
        private const int graspWallMinChance = 50;
        private int FindWallLoopCount;

        public bool IsClimbing
        {
            get { return isClimbing; }
        }
        /// <summary>
        /// true if player is climbing but trying to regain hold of wall
        /// </summary>
        public bool IsSlipping
        {
            get { return isSlipping; }
        }
        /// <summary>
        /// True if player just jumped from a wall
        /// </summary>
        public bool WallEject { get; private set; }

        void Start()
        {
            player = GameManager.Instance.PlayerEntity;
            playerMotor = GetComponent<PlayerMotor>();
            levitateMotor = GetComponent<LevitateMotor>();
            controller = GetComponent<CharacterController>();
            playerEnterExit = GetComponent<PlayerEnterExit>();
            acrobatMotor = GetComponent<AcrobatMotor>();
        }

        /// <summary>
        /// Perform climbing check, and if successful, start climbing movement.
        /// </summary>
        public void ClimbingCheck()
        {
            float startClimbHorizontalTolerance;
            float startClimbSkillCheckFrequency;
            bool airborneGraspWall = (!isClimbing && !isSlipping && acrobatMotor.Falling);
            
            if (airborneGraspWall)
            {
                startClimbHorizontalTolerance = 0.90f;
                startClimbSkillCheckFrequency = 5;
            }
            else
            {
                startClimbHorizontalTolerance = 0.12f;
                startClimbSkillCheckFrequency = 14;
            }

            bool inputAbortCondition;

            if (DaggerfallUnity.Settings.AdvancedClimbing)
            {
                // TODO: prevent crouch from toggling crouch when aborting climb
                inputAbortCondition = (InputManager.Instance.HasAction(InputManager.Actions.Crouch)
                                      || InputManager.Instance.HasAction(InputManager.Actions.Jump));
            }
            else
                inputAbortCondition = !InputManager.Instance.HasAction(InputManager.Actions.MoveForwards);
            
            // reset for next use
            WallEject = false;

            // Should we abort climbing?
            if (inputAbortCondition
                || (playerMotor.CollisionFlags & CollisionFlags.Sides) == 0
                || levitateMotor.IsLevitating
                || playerMotor.IsRiding
                // if we slipped and struck the ground
                || (isSlipping && ((playerMotor.CollisionFlags & CollisionFlags.Below) != 0)
                // don't do horizontal position check if already climbing
                || (!isClimbing && Vector2.Distance(lastHorizontalPosition, new Vector2(controller.transform.position.x, controller.transform.position.z)) > startClimbHorizontalTolerance)))
            {
                if (isClimbing && inputAbortCondition && DaggerfallUnity.Settings.AdvancedClimbing)
                    WallEject = true;
                isClimbing = false;
                isSlipping = false;
                atOutsideCorner = false;
                showClimbingModeMessage = true;
                climbingStartTimer = 0;

                // Reset position for horizontal distance check
                lastHorizontalPosition = new Vector2(controller.transform.position.x, controller.transform.position.z);
            }
            else // schedule climbing events
            {
                // schedule climbing start
                if (climbingStartTimer <= (playerMotor.systemTimerUpdatesPerSecond * startClimbSkillCheckFrequency))
                    climbingStartTimer += Time.deltaTime;
                else
                {
                    // automatic success if not falling
                    if (!airborneGraspWall)
                        StartClimbing();
                    // skill check to see if we catch the wall 
                    else if (SkillCheck(graspWallMinChance))
                        StartClimbing();
                    else
                        climbingStartTimer = 0;
                }

                // schedule climbing continues, Faster updates if slipping
                if (climbingContinueTimer <= (playerMotor.systemTimerUpdatesPerSecond * (isSlipping ? regainHoldSkillCheckFrequency : continueClimbingSkillCheckFrequency)))
                    climbingContinueTimer += Time.deltaTime;
                else
                {
                    climbingContinueTimer = 0;
                    // it's harder to regain hold while slipping than it is to continue climbing with a good hold on wall
                    if (!InputManager.Instance.HasAction(InputManager.Actions.MoveForwards)
                            && !InputManager.Instance.HasAction(InputManager.Actions.MoveBackwards)
                            && !InputManager.Instance.HasAction(InputManager.Actions.MoveLeft)
                            && !InputManager.Instance.HasAction(InputManager.Actions.MoveRight))
                        isSlipping = false;
                    else if (isSlipping)
                        isSlipping = !SkillCheck(regainHoldMinChance);
                    else
                        isSlipping = !SkillCheck(continueClimbMinChance);
                }
            }

            // execute schedule
            if (isClimbing)
            {
                // evalate the ledge direction
                GetClimbedWallInfo();

                ClimbMovement();

                // both variables represent similar situations, but different context
                acrobatMotor.Falling = isSlipping;
            }

        }

        /// <summary>
        /// Set climbing to true and show climbing mode message once
        /// </summary>
        private void StartClimbing()
        {
            if (!isClimbing)
            {
                if (showClimbingModeMessage)
                    DaggerfallUI.AddHUDText(UserInterfaceWindows.HardStrings.climbingMode);
                // Disable further showing of climbing mode message until current climb attempt is stopped
                // to keep it from filling message log
                showClimbingModeMessage = false;
                isClimbing = true;
            }
        }

        /// <summary>
        /// Physically check for wall in front of player and Set horizontal direction of that wall 
        /// </summary>
        private void GetClimbedWallInfo()
        {
            RaycastHit hit;

            Vector3 p1 = controller.transform.position + controller.center + Vector3.up * -controller.height * 0.40f;
            Vector3 p2 = p1 + Vector3.up * controller.height;

            // decide what direction to look towards to get the ledge direction vector
            Vector3 wallDirection;
            if (ledgeDirection == Vector3.zero)
                wallDirection = controller.transform.forward;
            else if (!atOutsideCorner)
                wallDirection = ledgeDirection;
            else
                wallDirection = -cornerNormalRay.direction;
            // Cast character controller shape forward to see if it is about to hit anything.
            Debug.DrawRay(controller.transform.position, wallDirection, Color.black);
            if (Physics.CapsuleCast(p1, p2, controller.radius, wallDirection, out hit, 0.20f))
            {
                // TODO: is ledge direction getting set correctly after wrapping?
                ledgeDirection = -hit.normal;

                // align origin of wall ray with y height of controller
                // direction can be adjusted when we have a side movement direction
                myStrafeRay = new Ray(new Vector3(hit.point.x, controller.transform.position.y, hit.point.z), hit.normal);
            }
        }

        private bool GetAdjacentWallInfo(Vector3 origin, Vector3 direction, bool searchClockwise)
        {
            RaycastHit hit;
            float distance = direction.magnitude;

            // use recursion to raycast vectors to find the adjacent wall
            Debug.DrawRay(origin, direction, Color.green, Time.deltaTime);
            if (Physics.Raycast(origin, direction, out hit, distance) 
                && (hit.collider.gameObject.GetComponent<MeshCollider>() != null))
            {
                Debug.DrawRay(hit.point, hit.normal);

                // need to assign the found wall's normal to a member level variable
                adjacentLedgeDirection = -hit.normal;

                //if (atOutsideCorner)

                if (searchClockwise)
                    adjacentWallRay = new Ray(hit.point, Vector3.Cross(hit.normal, Vector3.up));
                else
                    adjacentWallRay = new Ray(hit.point, Vector3.Cross(Vector3.up, hit.normal));

                Debug.DrawRay(adjacentWallRay.origin, adjacentWallRay.direction, Color.cyan);


                FindWallLoopCount = 0;
                return true;
            }
            else
            {
                FindWallLoopCount++;
                if (FindWallLoopCount < 3)
                {
                    // find next vector info now
                    Vector3 lastOrigin = origin;
                    origin = origin + direction;
                    Vector3 nextDirection = Vector3.zero; 

                    if (searchClockwise)
                        nextDirection = Vector3.Cross(lastOrigin - origin, Vector3.up).normalized * distance;
                    else
                        nextDirection = Vector3.Cross(Vector3.up, lastOrigin - origin).normalized * distance;

                    return GetAdjacentWallInfo(origin, nextDirection, searchClockwise);
                }
                FindWallLoopCount = 0;
                return false;
            }

        }
       

        /// <summary>
        /// Perform Climbing Movement
        /// </summary>
        private void ClimbMovement()
        {
            // Try to move up and forwards at same time
            // This helps player smoothly mantle the top of whatever they are climbing
            // Horizontal distance check in ClimbingCheck() will cancel climb once player mantles
            // This has the happy side effect of fixing issue where player climbs endlessly into sky or starting to climb when not facing wall

            // Climbing effect states "target can climb twice as well" - doubling climbing speed
            float climbingBoost = player.IsEnhancedClimbing ? 2f : 1f;
            // if strafing to either side, this will be set so we can check for wrap-around corners.
            Vector3 checkDirection = Vector3.zero;
            bool adjacentWallFound = false;
            bool outsideCornerFound = false; // experimental
            bool insideCornerFound = false; // experimental

            if (!isSlipping)
            {
                float climbScalar = (playerMotor.Speed / 3) * climbingBoost;
                moveDirection = Vector3.zero;
                bool movedForward = InputManager.Instance.HasAction(InputManager.Actions.MoveForwards);
                bool movedBackward = InputManager.Instance.HasAction(InputManager.Actions.MoveBackwards);
                bool movedLeft = InputManager.Instance.HasAction(InputManager.Actions.MoveLeft);
                bool movedRight = InputManager.Instance.HasAction(InputManager.Actions.MoveRight);

                if (DaggerfallUnity.Settings.AdvancedClimbing)
                {

                    // TODO: something is preventing player from climbing up after wrapping... 
                    RaycastHit hit = new RaycastHit();
                    if (!atOutsideCorner &&
                        (movedForward 
                        // don't stop if almost done climbing, prevents Climbing Teleportation bug
                        // only raycasts if player released forward key 
                        // make sure we aren't hitting a meshcollider
                        || (!Physics.Raycast(controller.transform.position, ledgeDirection, out hit, 0.3f) 
                        || !hit.collider.gameObject.GetComponent<MeshCollider>())))
                    {
                        moveDirection.y = Vector3.up.y * climbScalar;
                    }
                    else if (movedBackward)
                        moveDirection.y = Vector3.down.y * climbScalar;

                    if (movedRight || movedLeft)
                    {
                        float checkScalar = controller.radius + 0.5f;
                        if (movedRight)
                            checkDirection = Vector3.Cross(Vector3.up, ledgeDirection).normalized;
                        else if (movedLeft)
                            checkDirection = Vector3.Cross(ledgeDirection, Vector3.up).normalized;

                        // adjust direction so it can intersect with adjacentWallRay
                        myStrafeRay.direction = checkDirection;
                        Debug.DrawRay(myStrafeRay.origin, myStrafeRay.direction, Color.red);

                        // perform check for adjacent wall
                        adjacentWallFound = GetAdjacentWallInfo(controller.transform.position, checkDirection * checkScalar, movedLeft);

                        Vector3 intersection;
                        Vector3 intersectionOrthogonal;
                        Vector3 wrapDirection = Vector3.zero; // direction to move while wrapping around wall
                        // did we find the wall corner intersection?
                        if (LineLineIntersection(out intersection, myStrafeRay.origin, myStrafeRay.direction, adjacentWallRay.origin, adjacentWallRay.direction))
                        {
                            intersectionOrthogonal = (-ledgeDirection - adjacentLedgeDirection).normalized;
                            Debug.DrawRay(intersection, intersectionOrthogonal, Color.yellow);
                            atOutsideCorner = ((myStrafeRay.origin - intersection).magnitude < 0.01f);
                            if (atOutsideCorner)
                            {
                                // perform outside wall wrap
                                if (movedRight)
                                    wrapDirection = Vector3.Cross(intersectionOrthogonal, Vector3.up).normalized;
                                else if (movedLeft)
                                    wrapDirection = Vector3.Cross(Vector3.up, intersectionOrthogonal).normalized;
                            }
                            // else if against inside corner, inside wall wrap 

                            cornerNormalRay = new Ray(intersection, intersectionOrthogonal);   
                        }

                        // exiting outside wall wrap?
                        if (atOutsideCorner && IsAlmostParallel(wrapDirection, adjacentWallRay.direction))
                        {
                            // strafe resume on new wall check here?
                            ledgeDirection = adjacentLedgeDirection;
                            wrapDirection = -adjacentWallRay.direction;
                            checkDirection = wrapDirection;
                            atOutsideCorner = false;
                        }

                        // the movement direction needs to update differently at outside corners
                        if (atOutsideCorner)
                        {
                            Debug.DrawRay(intersection, wrapDirection, Color.magenta);
                            moveDirection += wrapDirection * climbScalar;
                        }
                        else // move in wasd direction
                            moveDirection += checkDirection * climbScalar;

                    }
                    // need to add horizontal movement towards wall for collision
                    moveDirection.x += ledgeDirection.x * playerMotor.Speed;
                    moveDirection.z += ledgeDirection.z * playerMotor.Speed;
                }
                else // do normal climbing
                {
                    moveDirection = ledgeDirection * playerMotor.Speed;
                    moveDirection.y = Vector3.up.y * climbScalar;
                }
                    
            }
            else // do slipping down wall
            {
                acrobatMotor.CheckInitFall();
                acrobatMotor.ApplyGravity(ref moveDirection);
            }

            /* Did we find an outside or inside corner? If so we must override wasd 
             * movement and make rotational movement */

            controller.Move(moveDirection * Time.deltaTime);
            playerMotor.CollisionFlags = controller.collisionFlags;
        }

        private void TowardWallImpulse(ref Vector3 moveDirection)
        {
            
        }

        /// <summary>
        ///  Calculate the intersection point of two lines. Returns true if lines intersect, otherwise false.
        /// </summary>
        /// <param name="intersection">The calculated intersection, if found</param>
        /// <param name="linePoint1">Origin 1</param>
        /// <param name="lineVec1">Direction 1</param>
        /// <param name="linePoint2">Origin 2</param>
        /// <param name="lineVec2">Direction 2</param>
        /// <returns>Returns true if lines intersect, otherwise false</returns>
        private bool LineLineIntersection(out Vector3 intersection, Vector3 linePoint1, Vector3 lineVec1, Vector3 linePoint2, Vector3 lineVec2)
        {
            Vector3 lineVec3 = linePoint2 - linePoint1;
            Vector3 crossVec1and2 = Vector3.Cross(lineVec1, lineVec2);
            Vector3 crossVec3and2 = Vector3.Cross(lineVec3, lineVec2);

            float planarFactor = Vector3.Dot(lineVec3, crossVec1and2);

            //is coplanar, and not parallel
            if (Mathf.Abs(planarFactor) < 0.01f && crossVec1and2.sqrMagnitude > 0.01f)
            {
                float s = Vector3.Dot(crossVec3and2, crossVec1and2) / crossVec1and2.sqrMagnitude;
                intersection = linePoint1 + (lineVec1 * s);
                return true;
            }
            else
            {
                intersection = Vector3.zero;
                return false;
            }
        }

        private bool IsAlmostParallel( Vector3 lineVec1, Vector3 lineVec2)
        {
            if (Vector3.Cross(lineVec1, lineVec2).sqrMagnitude < 0.01f)
                return true;
            return false;
        }

        /// <summary>
        /// See if the player can pass a climbing skill check
        /// </summary>
        /// <returns>true if player passed climbing skill check</returns>
        private bool SkillCheck(int basePercentSuccess)
        {
            player.TallySkill(DFCareer.Skills.Climbing, 1);
            int skill = player.Skills.GetLiveSkillValue(DFCareer.Skills.Climbing);
            if (player.Race == Entity.Races.Khajiit)
                skill += 30;

            // Climbing effect states "target can climb twice as well" - doubling effective skill after racial applied
            if (player.IsEnhancedClimbing)
                skill *= 2;

            // Clamp skill range
            skill = Mathf.Clamp(skill, 5, 95);

            // Skill Check
            float percentRolled = Mathf.Lerp(basePercentSuccess, 100, skill * .01f);

            if (percentRolled < UnityEngine.Random.Range(1, 101)) // Failed Check?
            {
                // Don't allow skill check to break climbing while swimming
                // This is another reason player can't climb out of water - any slip in climb will throw them back into swim mode
                // For now just pretend water is supporting player while they climb
                // It's not enough to check if they are swimming, need to check if their feet are above water. - MeteoricDragon
                var playerPos = controller.transform.position.y + (76 * MeshReader.GlobalScale) - 0.95f;
                var playerFootPos = playerPos - (controller.height / 2) - 1.20f; // to prevent player from failing to climb out of water
                var waterPos = playerEnterExit.blockWaterLevel * -1 * MeshReader.GlobalScale;
                if (playerFootPos >= waterPos) // prevent fail underwater
                    return false;
            }
            return true;
        }
    }
}


