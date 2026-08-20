// Who an audit row belongs to, resolved from the account's role.
//
// The reset endpoints serve drivers and dashboard staff alike and cannot tell
// which app called them, so the account's own role is what identifies the actor.
// Recording it at the time of the event keeps the row readable later, when the
// person may hold a different role or none at all.

import type { SupabaseClient } from "npm:@supabase/supabase-js@2";

const DRIVER_ROLE_ID = 2;

export type Actor = { actorType: string; actorRole: string | null };

/**
 * The actor fields for a user, given the role stored on their account.
 *
 * Drivers are recorded as `user` with the role the mobile app signs in under, so
 * they read the same as every other row the driver app writes. Everyone else is
 * dashboard staff, recorded under their own role name.
 */
export async function actorFor(
  service: SupabaseClient,
  roleId: number | null | undefined,
): Promise<Actor> {
  if (roleId === DRIVER_ROLE_ID) return { actorType: "user", actorRole: "app_driver" };
  if (roleId == null) return { actorType: "user", actorRole: null };

  const { data } = await service
    .from("roles")
    .select("role_name")
    .eq("role_id", roleId)
    .limit(1);

  // A role that cannot be read still identifies dashboard staff, which is more
  // than the surface alone says.
  return { actorType: "admin", actorRole: data?.[0]?.role_name ?? null };
}
