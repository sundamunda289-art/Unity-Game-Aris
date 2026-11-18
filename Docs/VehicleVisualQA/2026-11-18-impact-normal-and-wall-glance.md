# Vehicle collision direction: wall slide vs direct impact

## Audited bug

The old OnCollisionEnter callback interpreted Collision.relativeVelocity.magnitude
as crash speed. At 22 m/s, a car scraping sideways almost parallel to a wall
could receive substantial damage and a crash flash despite striking the wall
at only 0.25 m/s **along its normal**. Both cases had nearly the same full
relative-velocity magnitude.

## Correction

On a collision-entry callback (not each drive or render frame), inspect
Collision.contactCount and Collision.GetContact(i).normal. For each
contact, project relative velocity onto the contact normal and take the
maximum absolute normal component. That is the physical impact-speed proxy
for crash audio, >7m/s damage, and the short light/HUD pulse.

The existing >4m/s audio threshold, >7m/s wear threshold, 0.75s damage
cooldown, saved damage persistence, vehicle materials and body physics remain
unchanged. ContactPoint is a value type and GetContact(index) does not create
a managed contact-array like Collision.contacts (Unity 6 API).

Examples: 22m/s head-on into a wall -> 22m/s normal impact; 22m/s
tangential slide with 0.25m/s penetration -> 0.25m/s normal impact.
Multiple contact normals choose the strongest actual normal component.

This is a source/math regression for an offline mobile game; only a native
Unity Play Mode run can certify specific authored wall geometry, damage
telemetry and real-world-looking VFX.
