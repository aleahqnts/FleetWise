


SET statement_timeout = 0;
SET lock_timeout = 0;
SET idle_in_transaction_session_timeout = 0;
SET client_encoding = 'UTF8';
SET standard_conforming_strings = on;
SELECT pg_catalog.set_config('search_path', '', false);
SET check_function_bodies = false;
SET xmloption = content;
SET client_min_messages = warning;
SET row_security = off;


COMMENT ON SCHEMA "public" IS 'standard public schema';



CREATE EXTENSION IF NOT EXISTS "pg_stat_statements" WITH SCHEMA "extensions";






CREATE EXTENSION IF NOT EXISTS "pgcrypto" WITH SCHEMA "extensions";






CREATE EXTENSION IF NOT EXISTS "supabase_vault" WITH SCHEMA "vault";






CREATE EXTENSION IF NOT EXISTS "uuid-ossp" WITH SCHEMA "extensions";






CREATE TYPE "public"."account_status_enum" AS ENUM (
    'Activated',
    'Deactivated'
);


ALTER TYPE "public"."account_status_enum" OWNER TO "postgres";


CREATE TYPE "public"."checklist_status_enum" AS ENUM (
    'Passed',
    'Failed',
    'Pending',
    'Passed with Defects'
);


ALTER TYPE "public"."checklist_status_enum" OWNER TO "postgres";


CREATE TYPE "public"."maintenance_status_enum" AS ENUM (
    'Needs Attention',
    'Under Repair',
    'No Issues'
);


ALTER TYPE "public"."maintenance_status_enum" OWNER TO "postgres";


CREATE TYPE "public"."priority_enum" AS ENUM (
    'Normal',
    'High',
    'Urgent'
);


ALTER TYPE "public"."priority_enum" OWNER TO "postgres";


CREATE TYPE "public"."target_audience_enum" AS ENUM (
    'All',
    'Route',
    'Driver'
);


ALTER TYPE "public"."target_audience_enum" OWNER TO "postgres";


CREATE TYPE "public"."trip_status_enum" AS ENUM (
    'Not Yet Started',
    'Active',
    'Completed',
    'Assignment Issue',
    'Pending'
);


ALTER TYPE "public"."trip_status_enum" OWNER TO "postgres";


CREATE TYPE "public"."vehicle_status_enum" AS ENUM (
    'Ready to Deploy',
    'Flagged',
    'Pending',
    'On Trip'
);


ALTER TYPE "public"."vehicle_status_enum" OWNER TO "postgres";


CREATE OR REPLACE FUNCTION "public"."audit_log_immutable"() RETURNS "trigger"
    LANGUAGE "plpgsql"
    AS $$
begin
  raise exception 'audit_log is append-only: % is not permitted', TG_OP
    using hint = 'History cannot be rewritten or trimmed. Removing this guard '
               || 'requires explicitly dropping/disabling the trigger, which is '
               || 'itself a deliberate, visible act.';
end $$;


ALTER FUNCTION "public"."audit_log_immutable"() OWNER TO "postgres";


CREATE OR REPLACE FUNCTION "public"."audit_password_change"() RETURNS "trigger"
    LANGUAGE "plpgsql" SECURITY DEFINER
    SET "search_path" TO 'public'
    AS $$
declare
  v_claims json;
  v_role   text;
begin
  v_claims := nullif(current_setting('request.jwt.claims', true), '')::json;
  v_role   := coalesce(v_claims->>'role', 'db');
  insert into public.audit_log
    (actor_type, actor_id, actor_role, action, target_table, target_id, source, outcome, summary)
  values
    (case v_role when 'app_driver' then 'user' when 'service_role' then 'admin' else 'system' end,
     v_claims->>'user_id', v_role, 'password_hash_changed', 'users', new.user_id::text,
     'db', 'ok', 'Password hash changed for user ' || new.user_id);
  return new;
end $$;


ALTER FUNCTION "public"."audit_password_change"() OWNER TO "postgres";


CREATE OR REPLACE FUNCTION "public"."audit_row_change"() RETURNS "trigger"
    LANGUAGE "plpgsql" SECURITY DEFINER
    SET "search_path" TO 'public'
    AS $$
declare
  v_claims json;
  v_role   text;
  v_actor_type text;
  v_actor_id   text;
  v_old jsonb;
  v_new jsonb;
  v_pk  text;
  v_summary text;
begin
  v_claims := nullif(current_setting('request.jwt.claims', true), '')::json;
  v_role   := coalesce(v_claims->>'role', 'db');

  v_actor_type := case v_role
    when 'app_driver'   then 'user'
    when 'app_camera'   then 'device'
    when 'service_role' then 'admin'   -- web/edge via service key (10b adds the admin name)
    when 'anon'         then 'anon'
    else 'system'
  end;
  v_actor_id := coalesce(v_claims->>'user_id', v_claims->>'device_id');

  if TG_OP <> 'INSERT' then v_old := to_jsonb(OLD) - 'password_hash'; end if;
  if TG_OP <> 'DELETE' then v_new := to_jsonb(NEW) - 'password_hash'; end if;

  v_pk := coalesce(v_new->>'user_id', v_old->>'user_id',
                   v_new->>'device_id', v_old->>'device_id');

  -- Human line. device_config gets a purpose-built one: "Update on device_config"
  -- means nothing to an auditor, but "counting line changed" is the event that
  -- moves passenger counts (and therefore revenue figures).
  if TG_TABLE_NAME = 'device_config' then
    v_summary := 'Camera ' || coalesce(v_pk, '?') || ': '
      || case
           when TG_OP = 'INSERT' then 'config created'
           when TG_OP = 'DELETE' then 'config deleted'
           when (v_old->>'line_ax') is distinct from (v_new->>'line_ax')
             or (v_old->>'line_ay') is distinct from (v_new->>'line_ay')
             or (v_old->>'line_bx') is distinct from (v_new->>'line_bx')
             or (v_old->>'line_by') is distinct from (v_new->>'line_by')
             then 'counting line moved'
           when (v_old->>'inward_sign') is distinct from (v_new->>'inward_sign')
             then 'boarding side flipped'
           when (v_old->>'use_back_camera') is distinct from (v_new->>'use_back_camera')
             then 'lens switched'
           else 'config changed'
         end
      || ' (v' || coalesce(v_new->>'version', v_old->>'version', '?')
      || ', by ' || coalesce(v_new->>'updated_by', 'unknown') || ')';
  else
    v_summary := initcap(TG_OP) || ' on ' || TG_TABLE_NAME || ' ' || coalesce(v_pk, '?')
      || case when v_actor_id is not null
              then ' by ' || v_actor_type || ' ' || v_actor_id else '' end;
  end if;

  insert into public.audit_log
    (actor_type, actor_id, actor_role, action, target_table, target_id,
     source, outcome, summary, changes)
  values
    (v_actor_type, v_actor_id, v_role, lower(TG_OP), TG_TABLE_NAME, v_pk,
     'db', 'ok', v_summary,
     jsonb_strip_nulls(jsonb_build_object('old', v_old, 'new', v_new)));

  if TG_OP = 'DELETE' then return OLD; end if;
  return NEW;
end $$;


ALTER FUNCTION "public"."audit_row_change"() OWNER TO "postgres";


CREATE OR REPLACE FUNCTION "public"."camera_vehicle"() RETURNS "text"
    LANGUAGE "sql" STABLE SECURITY DEFINER
    SET "search_path" TO 'public'
    AS $$
  select vehicle_id from vehicles where counter_device_id = public.jwt_dev() limit 1
$$;


ALTER FUNCTION "public"."camera_vehicle"() OWNER TO "postgres";


CREATE OR REPLACE FUNCTION "public"."driver_active_camera"() RETURNS "text"
    LANGUAGE "sql" STABLE SECURITY DEFINER
    SET "search_path" TO 'public'
    AS $$
  select v.counter_device_id
  from trips t
  join vehicles v on v.vehicle_id = t.vehicle_id
  where t.driver_id = public.jwt_uid()
    and t.trip_status = 'Active'
    and v.counter_device_id is not null
  limit 1
$$;


ALTER FUNCTION "public"."driver_active_camera"() OWNER TO "postgres";


CREATE OR REPLACE FUNCTION "public"."driver_is_active"() RETURNS boolean
    LANGUAGE "sql" STABLE SECURITY DEFINER
    SET "search_path" TO 'public'
    AS $$
  select exists (
    select 1 from users
    where user_id = public.jwt_uid() and account_status = 'Activated'
  )
$$;


ALTER FUNCTION "public"."driver_is_active"() OWNER TO "postgres";


CREATE OR REPLACE FUNCTION "public"."jwt_dev"() RETURNS "text"
    LANGUAGE "sql" STABLE
    AS $$
  select nullif(current_setting('request.jwt.claims', true), '')::json ->> 'device_id'
$$;


ALTER FUNCTION "public"."jwt_dev"() OWNER TO "postgres";


CREATE OR REPLACE FUNCTION "public"."jwt_uid"() RETURNS integer
    LANGUAGE "sql" STABLE
    AS $$
  select nullif(nullif(current_setting('request.jwt.claims', true), '')::json ->> 'user_id', '')::int
$$;


ALTER FUNCTION "public"."jwt_uid"() OWNER TO "postgres";

SET default_tablespace = '';

SET default_table_access_method = "heap";


CREATE TABLE IF NOT EXISTS "public"."audit_log" (
    "id" bigint NOT NULL,
    "occurred_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    "actor_type" "text" NOT NULL,
    "actor_id" "text",
    "actor_role" "text",
    "action" "text" NOT NULL,
    "target_table" "text",
    "target_id" "text",
    "source" "text" NOT NULL,
    "outcome" "text" DEFAULT 'ok'::"text" NOT NULL,
    "summary" "text",
    "changes" "jsonb",
    "ip" "text",
    "request_id" "text"
);


ALTER TABLE "public"."audit_log" OWNER TO "postgres";


ALTER TABLE "public"."audit_log" ALTER COLUMN "id" ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME "public"."audit_log_id_seq"
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE IF NOT EXISTS "public"."bus_checklist" (
    "checklist_id" integer NOT NULL,
    "trip_id" character varying(20) NOT NULL,
    "vehicle_id" character varying(20) NOT NULL,
    "driver_id" integer NOT NULL,
    "submitted_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    "exterior_inspection" "jsonb" NOT NULL,
    "engine_compartment" "jsonb" NOT NULL,
    "interior_inspection" "jsonb" NOT NULL,
    "brake_safety" "jsonb" NOT NULL,
    "passenger_systems" "jsonb" NOT NULL,
    "checklist_status" "public"."checklist_status_enum" DEFAULT 'Pending'::"public"."checklist_status_enum" NOT NULL,
    "notes" "text"
);


ALTER TABLE "public"."bus_checklist" OWNER TO "postgres";


ALTER TABLE "public"."bus_checklist" ALTER COLUMN "checklist_id" ADD GENERATED BY DEFAULT AS IDENTITY (
    SEQUENCE NAME "public"."bus_checklist_checklist_id_seq"
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE IF NOT EXISTS "public"."checklist_items" (
    "item_id" integer NOT NULL,
    "section_key" "text" NOT NULL,
    "section_title" "text" NOT NULL,
    "label" "text" NOT NULL,
    "is_critical" boolean DEFAULT false NOT NULL,
    "sort_order" integer NOT NULL,
    "active" boolean DEFAULT true NOT NULL
);


ALTER TABLE "public"."checklist_items" OWNER TO "postgres";


ALTER TABLE "public"."checklist_items" ALTER COLUMN "item_id" ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME "public"."checklist_items_item_id_seq"
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE IF NOT EXISTS "public"."device_config" (
    "device_id" "text" NOT NULL,
    "line_ax" real,
    "line_ay" real,
    "line_bx" real,
    "line_by" real,
    "inward_sign" integer DEFAULT 1 NOT NULL,
    "use_back_camera" boolean DEFAULT false NOT NULL,
    "wake_requested_at" timestamp with time zone,
    "version" integer DEFAULT 0 NOT NULL,
    "updated_by" "text",
    "updated_at" timestamp with time zone DEFAULT "now"()
);


ALTER TABLE "public"."device_config" OWNER TO "postgres";


CREATE TABLE IF NOT EXISTS "public"."device_status" (
    "device_id" "text" NOT NULL,
    "last_seen" timestamp with time zone,
    "wake_state" "text" DEFAULT 'idle'::"text" NOT NULL,
    "snapshot_ready_at" timestamp with time zone,
    "applied_at" timestamp with time zone,
    "config_version_applied" integer DEFAULT '-1'::integer NOT NULL
);


ALTER TABLE "public"."device_status" OWNER TO "postgres";


CREATE TABLE IF NOT EXISTS "public"."driver_availability" (
    "user_id" integer NOT NULL,
    "availability_status" character varying(20) DEFAULT 'Available'::character varying NOT NULL,
    "updated_at" timestamp without time zone,
    "reason" "text",
    CONSTRAINT "driver_availability_availability_status_check" CHECK ((("availability_status")::"text" = ANY ((ARRAY['Available'::character varying, 'Unavailable'::character varying])::"text"[])))
);


ALTER TABLE "public"."driver_availability" OWNER TO "postgres";


CREATE TABLE IF NOT EXISTS "public"."fare_config" (
    "id" integer DEFAULT 1 NOT NULL,
    "standard_fare" numeric(10,2) NOT NULL,
    "updated_at" timestamp with time zone DEFAULT "now"()
);


ALTER TABLE "public"."fare_config" OWNER TO "postgres";


CREATE TABLE IF NOT EXISTS "public"."maintenance_items" (
    "item_id" bigint NOT NULL,
    "log_id" integer NOT NULL,
    "label" "text" NOT NULL,
    "is_critical" boolean DEFAULT false NOT NULL,
    "source" "text" DEFAULT 'manual'::"text" NOT NULL,
    "state" "text" DEFAULT 'open'::"text" NOT NULL,
    "closed_at" timestamp with time zone,
    "closed_by" "text",
    "note" "text",
    "created_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    CONSTRAINT "maintenance_items_critical_not_dismissed" CHECK ((NOT ("is_critical" AND ("state" = 'dismissed'::"text")))),
    CONSTRAINT "maintenance_items_source_check" CHECK (("source" = ANY (ARRAY['checklist'::"text", 'manual'::"text"]))),
    CONSTRAINT "maintenance_items_state_check" CHECK (("state" = ANY (ARRAY['open'::"text", 'fixed'::"text", 'dismissed'::"text"])))
);


ALTER TABLE "public"."maintenance_items" OWNER TO "postgres";


COMMENT ON TABLE "public"."maintenance_items" IS 'The faults being worked under one maintenance_logs order, one row per fault.';



COMMENT ON COLUMN "public"."maintenance_items"."is_critical" IS 'Whether failing this grounds the bus. Set from checklist_items; hand-typed items are never critical.';



COMMENT ON COLUMN "public"."maintenance_items"."state" IS 'open until closed as fixed, or dismissed when the fault was not real.';



ALTER TABLE "public"."maintenance_items" ALTER COLUMN "item_id" ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME "public"."maintenance_items_item_id_seq"
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE IF NOT EXISTS "public"."maintenance_logs" (
    "log_id" integer NOT NULL,
    "checklist_id" integer,
    "vehicle_id" character varying(20) NOT NULL,
    "trip_id" character varying(20),
    "issue_details" "jsonb" NOT NULL,
    "maintenance_status" "public"."maintenance_status_enum" DEFAULT 'Needs Attention'::"public"."maintenance_status_enum" NOT NULL,
    "created_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    "resolved_at" timestamp with time zone,
    "remarks" "text",
    "verified_by" "text"
);


ALTER TABLE "public"."maintenance_logs" OWNER TO "postgres";


ALTER TABLE "public"."maintenance_logs" ALTER COLUMN "log_id" ADD GENERATED BY DEFAULT AS IDENTITY (
    SEQUENCE NAME "public"."maintenance_logs_log_id_seq"
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE IF NOT EXISTS "public"."maintenance_notes" (
    "note_id" bigint NOT NULL,
    "log_id" integer NOT NULL,
    "author_id" integer,
    "author_name" "text",
    "action" "text",
    "note" "text",
    "created_at" timestamp with time zone DEFAULT "now"() NOT NULL
);


ALTER TABLE "public"."maintenance_notes" OWNER TO "postgres";


ALTER TABLE "public"."maintenance_notes" ALTER COLUMN "note_id" ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME "public"."maintenance_notes_note_id_seq"
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE IF NOT EXISTS "public"."messages" (
    "message_id" integer NOT NULL,
    "sender_id" integer NOT NULL,
    "target_audience" "public"."target_audience_enum" NOT NULL,
    "target_id" character varying(20),
    "subject" character varying(255),
    "body" "text" NOT NULL,
    "priority" "public"."priority_enum" DEFAULT 'Normal'::"public"."priority_enum" NOT NULL,
    "created_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    "is_read" boolean DEFAULT false NOT NULL
);


ALTER TABLE "public"."messages" OWNER TO "postgres";


ALTER TABLE "public"."messages" ALTER COLUMN "message_id" ADD GENERATED BY DEFAULT AS IDENTITY (
    SEQUENCE NAME "public"."messages_message_id_seq"
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE IF NOT EXISTS "public"."password_reset_otp" (
    "id" bigint NOT NULL,
    "user_id" integer NOT NULL,
    "otp_hash" "text" NOT NULL,
    "expires_at" timestamp with time zone NOT NULL,
    "attempts" integer DEFAULT 0 NOT NULL,
    "consumed_at" timestamp with time zone,
    "completed_at" timestamp with time zone,
    "created_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    "ip" "text"
);


ALTER TABLE "public"."password_reset_otp" OWNER TO "postgres";


ALTER TABLE "public"."password_reset_otp" ALTER COLUMN "id" ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME "public"."password_reset_otp_id_seq"
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE IF NOT EXISTS "public"."roles" (
    "role_id" integer NOT NULL,
    "role_name" character varying(50) NOT NULL,
    "access_level" character varying(50) NOT NULL,
    "web_permissions" "jsonb" NOT NULL,
    "mobile_permissions" "jsonb" NOT NULL
);


ALTER TABLE "public"."roles" OWNER TO "postgres";


ALTER TABLE "public"."roles" ALTER COLUMN "role_id" ADD GENERATED BY DEFAULT AS IDENTITY (
    SEQUENCE NAME "public"."roles_role_id_seq"
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE IF NOT EXISTS "public"."routes" (
    "route_id" integer NOT NULL,
    "route_name" character varying(100) NOT NULL,
    "origin" character varying(100) NOT NULL,
    "destination" character varying(100) NOT NULL,
    "created_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    "updated_at" timestamp with time zone,
    "waypoints_json" "text",
    "stops_json" "text"
);


ALTER TABLE "public"."routes" OWNER TO "postgres";


ALTER TABLE "public"."routes" ALTER COLUMN "route_id" ADD GENERATED BY DEFAULT AS IDENTITY (
    SEQUENCE NAME "public"."routes_route_id_seq"
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE IF NOT EXISTS "public"."telemetry_data" (
    "telemetry_id" bigint NOT NULL,
    "trip_id" character varying(20) NOT NULL,
    "latitude" numeric(10,8) NOT NULL,
    "longitude" numeric(11,8) NOT NULL,
    "total_passengers" integer NOT NULL,
    "speed" numeric(5,2),
    "heading" double precision,
    "timestamp" timestamp with time zone DEFAULT "now"() NOT NULL
);


ALTER TABLE "public"."telemetry_data" OWNER TO "postgres";


ALTER TABLE "public"."telemetry_data" ALTER COLUMN "telemetry_id" ADD GENERATED BY DEFAULT AS IDENTITY (
    SEQUENCE NAME "public"."telemetry_data_telemetry_id_seq"
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE SEQUENCE IF NOT EXISTS "public"."trip_id_seq"
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


ALTER SEQUENCE "public"."trip_id_seq" OWNER TO "postgres";


CREATE SEQUENCE IF NOT EXISTS "public"."trip_seq"
    START WITH 26001
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


ALTER SEQUENCE "public"."trip_seq" OWNER TO "postgres";


CREATE TABLE IF NOT EXISTS "public"."trips" (
    "trip_id" character varying(20) DEFAULT ('TRIP'::"text" || "lpad"(("nextval"('"public"."trip_seq"'::"regclass"))::"text", 6, '0'::"text")) NOT NULL,
    "date" "date" NOT NULL,
    "shift_type" character varying(50) NOT NULL,
    "shift_start_time" time without time zone NOT NULL,
    "shift_end_time" time without time zone NOT NULL,
    "route_id" integer NOT NULL,
    "vehicle_id" character varying(20) NOT NULL,
    "driver_id" integer NOT NULL,
    "trip_status" "public"."trip_status_enum" DEFAULT 'Not Yet Started'::"public"."trip_status_enum" NOT NULL,
    "estimated_revenue" numeric(10,2) DEFAULT 0.00 NOT NULL,
    "total_boarded" integer DEFAULT 0 NOT NULL,
    "actual_start_time" timestamp with time zone,
    "actual_end_time" timestamp with time zone,
    "is_simulated" boolean DEFAULT false NOT NULL,
    "count_heartbeat" timestamp with time zone,
    "counter_device_id" "text"
);


ALTER TABLE "public"."trips" OWNER TO "postgres";


CREATE TABLE IF NOT EXISTS "public"."users" (
    "user_id" integer NOT NULL,
    "first_name" character varying(50) NOT NULL,
    "middle_name" character varying(50),
    "last_name" character varying(50) NOT NULL,
    "email_address" character varying(100) NOT NULL,
    "password_hash" character varying(255) NOT NULL,
    "role_id" integer NOT NULL,
    "account_status" "public"."account_status_enum" DEFAULT 'Activated'::"public"."account_status_enum" NOT NULL,
    "created_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    "updated_at" timestamp with time zone,
    "last_login" timestamp with time zone,
    "contact_number" "text",
    "address" "text",
    "emergency_contact_name" "text",
    "emergency_contact_number" "text"
);


ALTER TABLE "public"."users" OWNER TO "postgres";


CREATE OR REPLACE VIEW "public"."users_app" AS
 SELECT "user_id",
    "first_name",
    "middle_name",
    "last_name",
    "email_address",
    "role_id",
    "account_status",
    "contact_number",
    "address",
    "emergency_contact_name",
    "emergency_contact_number",
    "created_at",
    "updated_at",
    "last_login"
   FROM "public"."users"
  WHERE ("user_id" = COALESCE("public"."jwt_uid"(), "user_id"))
  WITH CASCADED CHECK OPTION;


ALTER VIEW "public"."users_app" OWNER TO "postgres";


ALTER TABLE "public"."users" ALTER COLUMN "user_id" ADD GENERATED BY DEFAULT AS IDENTITY (
    SEQUENCE NAME "public"."users_user_id_seq"
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);



CREATE TABLE IF NOT EXISTS "public"."vehicles" (
    "vehicle_id" character varying(20) NOT NULL,
    "plate_number" character varying(20) NOT NULL,
    "route_id" integer,
    "capacity" integer NOT NULL,
    "vehicle_status" "public"."vehicle_status_enum" DEFAULT 'Ready to Deploy'::"public"."vehicle_status_enum" NOT NULL,
    "last_maintenance_date" "date",
    "created_at" timestamp with time zone DEFAULT "now"() NOT NULL,
    "updated_at" timestamp with time zone,
    "out_of_service" boolean DEFAULT false NOT NULL,
    "counter_device_id" "text",
    "retired_at" timestamp with time zone,
    "retired_reason" "text"
);


ALTER TABLE "public"."vehicles" OWNER TO "postgres";


COMMENT ON COLUMN "public"."vehicles"."retired_at" IS 'When the bus left the fleet for good. Null means it is still in service.';



COMMENT ON COLUMN "public"."vehicles"."retired_reason" IS 'Why it was retired, as entered by an administrator.';



ALTER TABLE ONLY "public"."audit_log"
    ADD CONSTRAINT "audit_log_pkey" PRIMARY KEY ("id");



ALTER TABLE ONLY "public"."bus_checklist"
    ADD CONSTRAINT "bus_checklist_pkey" PRIMARY KEY ("checklist_id");



ALTER TABLE ONLY "public"."checklist_items"
    ADD CONSTRAINT "checklist_items_pkey" PRIMARY KEY ("item_id");



ALTER TABLE ONLY "public"."checklist_items"
    ADD CONSTRAINT "checklist_items_section_key_label_key" UNIQUE ("section_key", "label");



ALTER TABLE ONLY "public"."device_config"
    ADD CONSTRAINT "device_config_pkey" PRIMARY KEY ("device_id");



ALTER TABLE ONLY "public"."device_status"
    ADD CONSTRAINT "device_status_pkey" PRIMARY KEY ("device_id");



ALTER TABLE ONLY "public"."driver_availability"
    ADD CONSTRAINT "driver_availability_pkey" PRIMARY KEY ("user_id");



ALTER TABLE ONLY "public"."fare_config"
    ADD CONSTRAINT "fare_config_pkey" PRIMARY KEY ("id");



ALTER TABLE ONLY "public"."maintenance_items"
    ADD CONSTRAINT "maintenance_items_pkey" PRIMARY KEY ("item_id");



ALTER TABLE ONLY "public"."maintenance_logs"
    ADD CONSTRAINT "maintenance_logs_pkey" PRIMARY KEY ("log_id");



ALTER TABLE ONLY "public"."maintenance_notes"
    ADD CONSTRAINT "maintenance_notes_pkey" PRIMARY KEY ("note_id");



ALTER TABLE ONLY "public"."messages"
    ADD CONSTRAINT "messages_pkey" PRIMARY KEY ("message_id");



ALTER TABLE ONLY "public"."password_reset_otp"
    ADD CONSTRAINT "password_reset_otp_pkey" PRIMARY KEY ("id");



ALTER TABLE ONLY "public"."roles"
    ADD CONSTRAINT "roles_pkey" PRIMARY KEY ("role_id");



ALTER TABLE ONLY "public"."routes"
    ADD CONSTRAINT "routes_pkey" PRIMARY KEY ("route_id");



ALTER TABLE ONLY "public"."telemetry_data"
    ADD CONSTRAINT "telemetry_data_pkey" PRIMARY KEY ("telemetry_id");



ALTER TABLE ONLY "public"."trips"
    ADD CONSTRAINT "trips_pkey" PRIMARY KEY ("trip_id");



ALTER TABLE ONLY "public"."users"
    ADD CONSTRAINT "users_email_address_key" UNIQUE ("email_address");



ALTER TABLE ONLY "public"."users"
    ADD CONSTRAINT "users_pkey" PRIMARY KEY ("user_id");



ALTER TABLE ONLY "public"."vehicles"
    ADD CONSTRAINT "vehicles_pkey" PRIMARY KEY ("vehicle_id");



ALTER TABLE ONLY "public"."vehicles"
    ADD CONSTRAINT "vehicles_plate_number_key" UNIQUE ("plate_number");



CREATE INDEX "idx_audit_action" ON "public"."audit_log" USING "btree" ("action", "occurred_at" DESC);



CREATE INDEX "idx_audit_actor" ON "public"."audit_log" USING "btree" ("actor_id", "occurred_at" DESC);



CREATE INDEX "idx_audit_target" ON "public"."audit_log" USING "btree" ("target_table", "target_id");



CREATE INDEX "idx_audit_time" ON "public"."audit_log" USING "btree" ("occurred_at" DESC);



CREATE INDEX "idx_checklist_items_order" ON "public"."checklist_items" USING "btree" ("active", "sort_order");



CREATE INDEX "idx_maintenance_items_open" ON "public"."maintenance_items" USING "btree" ("log_id") WHERE ("state" = 'open'::"text");



CREATE INDEX "idx_pwreset_ip" ON "public"."password_reset_otp" USING "btree" ("ip", "created_at" DESC);



CREATE INDEX "idx_pwreset_time" ON "public"."password_reset_otp" USING "btree" ("created_at" DESC);



CREATE INDEX "idx_pwreset_user" ON "public"."password_reset_otp" USING "btree" ("user_id", "created_at" DESC);



CREATE INDEX "idx_vehicles_retired" ON "public"."vehicles" USING "btree" ("retired_at");



CREATE INDEX "ix_maintenance_notes_log" ON "public"."maintenance_notes" USING "btree" ("log_id");



CREATE UNIQUE INDEX "uq_maintenance_items_label" ON "public"."maintenance_items" USING "btree" ("log_id", "lower"("label"));



CREATE OR REPLACE TRIGGER "trg_audit_devcfg_ins_del" AFTER INSERT OR DELETE ON "public"."device_config" FOR EACH ROW EXECUTE FUNCTION "public"."audit_row_change"();



CREATE OR REPLACE TRIGGER "trg_audit_devcfg_upd" AFTER UPDATE ON "public"."device_config" FOR EACH ROW WHEN (((("to_jsonb"("old".*) - 'wake_requested_at'::"text") - 'updated_at'::"text") IS DISTINCT FROM (("to_jsonb"("new".*) - 'wake_requested_at'::"text") - 'updated_at'::"text"))) EXECUTE FUNCTION "public"."audit_row_change"();



CREATE OR REPLACE TRIGGER "trg_audit_log_no_delete" BEFORE DELETE ON "public"."audit_log" FOR EACH ROW EXECUTE FUNCTION "public"."audit_log_immutable"();



CREATE OR REPLACE TRIGGER "trg_audit_log_no_truncate" BEFORE TRUNCATE ON "public"."audit_log" FOR EACH STATEMENT EXECUTE FUNCTION "public"."audit_log_immutable"();



CREATE OR REPLACE TRIGGER "trg_audit_log_no_update" BEFORE UPDATE ON "public"."audit_log" FOR EACH ROW EXECUTE FUNCTION "public"."audit_log_immutable"();



CREATE OR REPLACE TRIGGER "trg_audit_users_ins_del" AFTER INSERT OR DELETE ON "public"."users" FOR EACH ROW EXECUTE FUNCTION "public"."audit_row_change"();



CREATE OR REPLACE TRIGGER "trg_audit_users_pwd" AFTER UPDATE OF "password_hash" ON "public"."users" FOR EACH ROW WHEN ((("old"."password_hash")::"text" IS DISTINCT FROM ("new"."password_hash")::"text")) EXECUTE FUNCTION "public"."audit_password_change"();



CREATE OR REPLACE TRIGGER "trg_audit_users_upd" AFTER UPDATE ON "public"."users" FOR EACH ROW WHEN ((((("to_jsonb"("old".*) - 'password_hash'::"text") - 'last_login'::"text") - 'updated_at'::"text") IS DISTINCT FROM ((("to_jsonb"("new".*) - 'password_hash'::"text") - 'last_login'::"text") - 'updated_at'::"text"))) EXECUTE FUNCTION "public"."audit_row_change"();



ALTER TABLE ONLY "public"."bus_checklist"
    ADD CONSTRAINT "bus_checklist_driver_id_fkey" FOREIGN KEY ("driver_id") REFERENCES "public"."users"("user_id") ON DELETE RESTRICT;



ALTER TABLE ONLY "public"."bus_checklist"
    ADD CONSTRAINT "bus_checklist_trip_id_fkey" FOREIGN KEY ("trip_id") REFERENCES "public"."trips"("trip_id") ON DELETE CASCADE;



ALTER TABLE ONLY "public"."bus_checklist"
    ADD CONSTRAINT "bus_checklist_vehicle_id_fkey" FOREIGN KEY ("vehicle_id") REFERENCES "public"."vehicles"("vehicle_id") ON DELETE RESTRICT;



ALTER TABLE ONLY "public"."driver_availability"
    ADD CONSTRAINT "driver_availability_user_id_fkey" FOREIGN KEY ("user_id") REFERENCES "public"."users"("user_id") ON DELETE CASCADE;



ALTER TABLE ONLY "public"."maintenance_items"
    ADD CONSTRAINT "maintenance_items_log_id_fkey" FOREIGN KEY ("log_id") REFERENCES "public"."maintenance_logs"("log_id") ON DELETE CASCADE;



ALTER TABLE ONLY "public"."maintenance_logs"
    ADD CONSTRAINT "maintenance_logs_checklist_id_fkey" FOREIGN KEY ("checklist_id") REFERENCES "public"."bus_checklist"("checklist_id") ON DELETE SET NULL;



ALTER TABLE ONLY "public"."maintenance_logs"
    ADD CONSTRAINT "maintenance_logs_trip_id_fkey" FOREIGN KEY ("trip_id") REFERENCES "public"."trips"("trip_id") ON DELETE SET NULL;



ALTER TABLE ONLY "public"."maintenance_logs"
    ADD CONSTRAINT "maintenance_logs_vehicle_id_fkey" FOREIGN KEY ("vehicle_id") REFERENCES "public"."vehicles"("vehicle_id") ON DELETE CASCADE;



ALTER TABLE ONLY "public"."maintenance_notes"
    ADD CONSTRAINT "maintenance_notes_author_id_fkey" FOREIGN KEY ("author_id") REFERENCES "public"."users"("user_id");



ALTER TABLE ONLY "public"."maintenance_notes"
    ADD CONSTRAINT "maintenance_notes_log_id_fkey" FOREIGN KEY ("log_id") REFERENCES "public"."maintenance_logs"("log_id") ON DELETE CASCADE;



ALTER TABLE ONLY "public"."messages"
    ADD CONSTRAINT "messages_sender_id_fkey" FOREIGN KEY ("sender_id") REFERENCES "public"."users"("user_id") ON DELETE RESTRICT;



ALTER TABLE ONLY "public"."password_reset_otp"
    ADD CONSTRAINT "password_reset_otp_user_id_fkey" FOREIGN KEY ("user_id") REFERENCES "public"."users"("user_id") ON DELETE CASCADE;



ALTER TABLE ONLY "public"."telemetry_data"
    ADD CONSTRAINT "telemetry_data_trip_id_fkey" FOREIGN KEY ("trip_id") REFERENCES "public"."trips"("trip_id") ON DELETE CASCADE;



ALTER TABLE ONLY "public"."trips"
    ADD CONSTRAINT "trips_driver_id_fkey" FOREIGN KEY ("driver_id") REFERENCES "public"."users"("user_id") ON DELETE RESTRICT;



ALTER TABLE ONLY "public"."trips"
    ADD CONSTRAINT "trips_route_id_fkey" FOREIGN KEY ("route_id") REFERENCES "public"."routes"("route_id") ON DELETE RESTRICT;



ALTER TABLE ONLY "public"."trips"
    ADD CONSTRAINT "trips_vehicle_id_fkey" FOREIGN KEY ("vehicle_id") REFERENCES "public"."vehicles"("vehicle_id") ON DELETE RESTRICT;



ALTER TABLE ONLY "public"."users"
    ADD CONSTRAINT "users_role_id_fkey" FOREIGN KEY ("role_id") REFERENCES "public"."roles"("role_id") ON DELETE RESTRICT;



ALTER TABLE ONLY "public"."vehicles"
    ADD CONSTRAINT "vehicles_route_id_fkey" FOREIGN KEY ("route_id") REFERENCES "public"."routes"("route_id") ON DELETE SET NULL;



CREATE POLICY "app full access" ON "public"."maintenance_notes" USING (true) WITH CHECK (true);



ALTER TABLE "public"."audit_log" ENABLE ROW LEVEL SECURITY;


ALTER TABLE "public"."bus_checklist" ENABLE ROW LEVEL SECURITY;


ALTER TABLE "public"."checklist_items" ENABLE ROW LEVEL SECURITY;


CREATE POLICY "checklist_items_read" ON "public"."checklist_items" FOR SELECT TO "app_driver" USING ("active");



ALTER TABLE "public"."device_config" ENABLE ROW LEVEL SECURITY;


ALTER TABLE "public"."device_status" ENABLE ROW LEVEL SECURITY;


ALTER TABLE "public"."driver_availability" ENABLE ROW LEVEL SECURITY;


ALTER TABLE "public"."fare_config" ENABLE ROW LEVEL SECURITY;


ALTER TABLE "public"."maintenance_items" ENABLE ROW LEVEL SECURITY;


ALTER TABLE "public"."maintenance_logs" ENABLE ROW LEVEL SECURITY;


ALTER TABLE "public"."maintenance_notes" ENABLE ROW LEVEL SECURITY;


ALTER TABLE "public"."messages" ENABLE ROW LEVEL SECURITY;


CREATE POLICY "p_avail_driver_all" ON "public"."driver_availability" TO "app_driver" USING (("user_id" = "public"."jwt_uid"())) WITH CHECK (("user_id" = "public"."jwt_uid"()));



CREATE POLICY "p_checklists_driver_insert" ON "public"."bus_checklist" FOR INSERT TO "app_driver" WITH CHECK ((EXISTS ( SELECT 1
   FROM "public"."trips" "t"
  WHERE ((("t"."trip_id")::"text" = ("bus_checklist"."trip_id")::"text") AND ("t"."driver_id" = "public"."jwt_uid"())))));



CREATE POLICY "p_checklists_driver_select" ON "public"."bus_checklist" FOR SELECT TO "app_driver" USING ((EXISTS ( SELECT 1
   FROM "public"."trips" "t"
  WHERE ((("t"."trip_id")::"text" = ("bus_checklist"."trip_id")::"text") AND ("t"."driver_id" = "public"."jwt_uid"())))));



CREATE POLICY "p_devcfg_camera_all" ON "public"."device_config" TO "app_camera" USING (("device_id" = "public"."jwt_dev"())) WITH CHECK (("device_id" = "public"."jwt_dev"()));



CREATE POLICY "p_devcfg_driver_select" ON "public"."device_config" FOR SELECT TO "app_driver" USING (("device_id" = "public"."driver_active_camera"()));



CREATE POLICY "p_devcfg_driver_update" ON "public"."device_config" FOR UPDATE TO "app_driver" USING ((("device_id" = "public"."driver_active_camera"()) AND "public"."driver_is_active"())) WITH CHECK (("device_id" = "public"."driver_active_camera"()));



CREATE POLICY "p_devstat_camera_all" ON "public"."device_status" TO "app_camera" USING (("device_id" = "public"."jwt_dev"())) WITH CHECK (("device_id" = "public"."jwt_dev"()));



CREATE POLICY "p_devstat_driver_select" ON "public"."device_status" FOR SELECT TO "app_driver" USING (("device_id" = "public"."driver_active_camera"()));



CREATE POLICY "p_fare_driver_select" ON "public"."fare_config" FOR SELECT TO "app_driver" USING (true);



CREATE POLICY "p_maintenance_driver_insert" ON "public"."maintenance_logs" FOR INSERT TO "app_driver" WITH CHECK ((EXISTS ( SELECT 1
   FROM "public"."trips" "t"
  WHERE ((("t"."trip_id")::"text" = ("maintenance_logs"."trip_id")::"text") AND ("t"."driver_id" = "public"."jwt_uid"())))));



CREATE POLICY "p_messages_driver_select" ON "public"."messages" FOR SELECT TO "app_driver" USING ((("lower"(COALESCE(("target_audience")::"text", ''::"text")) = 'all'::"text") OR (("lower"(("target_audience")::"text") = 'driver'::"text") AND (("target_id")::"text" = ("public"."jwt_uid"())::"text")) OR (("lower"(("target_audience")::"text") = 'route'::"text") AND (("target_id")::"text" IN ( SELECT ("t"."route_id")::"text" AS "route_id"
   FROM "public"."trips" "t"
  WHERE ("t"."driver_id" = "public"."jwt_uid"()))))));



CREATE POLICY "p_messages_driver_update" ON "public"."messages" FOR UPDATE TO "app_driver" USING ((("lower"(("target_audience")::"text") = 'driver'::"text") AND (("target_id")::"text" = ("public"."jwt_uid"())::"text")));



CREATE POLICY "p_routes_driver_select" ON "public"."routes" FOR SELECT TO "app_driver" USING (true);



CREATE POLICY "p_telemetry_driver_insert" ON "public"."telemetry_data" FOR INSERT TO "app_driver" WITH CHECK ((EXISTS ( SELECT 1
   FROM "public"."trips" "t"
  WHERE ((("t"."trip_id")::"text" = ("telemetry_data"."trip_id")::"text") AND ("t"."driver_id" = "public"."jwt_uid"())))));



CREATE POLICY "p_trips_camera_select" ON "public"."trips" FOR SELECT TO "app_camera" USING (((("vehicle_id")::"text" = "public"."camera_vehicle"()) OR ("counter_device_id" = "public"."jwt_dev"())));



CREATE POLICY "p_trips_camera_update" ON "public"."trips" FOR UPDATE TO "app_camera" USING ((((("vehicle_id")::"text" = "public"."camera_vehicle"()) AND ("trip_status" = 'Active'::"public"."trip_status_enum")) OR ("counter_device_id" = "public"."jwt_dev"()))) WITH CHECK (("counter_device_id" = "public"."jwt_dev"()));



CREATE POLICY "p_trips_driver_select" ON "public"."trips" FOR SELECT TO "app_driver" USING (("driver_id" = "public"."jwt_uid"()));



CREATE POLICY "p_trips_driver_update" ON "public"."trips" FOR UPDATE TO "app_driver" USING ((("driver_id" = "public"."jwt_uid"()) AND "public"."driver_is_active"())) WITH CHECK (("driver_id" = "public"."jwt_uid"()));



CREATE POLICY "p_vehicles_camera_select" ON "public"."vehicles" FOR SELECT TO "app_camera" USING (true);



CREATE POLICY "p_vehicles_camera_update" ON "public"."vehicles" FOR UPDATE TO "app_camera" USING ((("counter_device_id" IS NULL) OR ("counter_device_id" = "public"."jwt_dev"()))) WITH CHECK ((("counter_device_id" = "public"."jwt_dev"()) OR ("counter_device_id" IS NULL)));



CREATE POLICY "p_vehicles_driver_select" ON "public"."vehicles" FOR SELECT TO "app_driver" USING (true);



CREATE POLICY "p_vehicles_driver_update" ON "public"."vehicles" FOR UPDATE TO "app_driver" USING ((EXISTS ( SELECT 1
   FROM "public"."trips" "t"
  WHERE ((("t"."vehicle_id")::"text" = ("vehicles"."vehicle_id")::"text") AND ("t"."driver_id" = "public"."jwt_uid"())))));



ALTER TABLE "public"."password_reset_otp" ENABLE ROW LEVEL SECURITY;


ALTER TABLE "public"."routes" ENABLE ROW LEVEL SECURITY;


ALTER TABLE "public"."telemetry_data" ENABLE ROW LEVEL SECURITY;


ALTER TABLE "public"."trips" ENABLE ROW LEVEL SECURITY;


ALTER TABLE "public"."users" ENABLE ROW LEVEL SECURITY;


ALTER TABLE "public"."vehicles" ENABLE ROW LEVEL SECURITY;




ALTER PUBLICATION "supabase_realtime" OWNER TO "postgres";


GRANT USAGE ON SCHEMA "public" TO "postgres";
GRANT USAGE ON SCHEMA "public" TO "anon";
GRANT USAGE ON SCHEMA "public" TO "authenticated";
GRANT USAGE ON SCHEMA "public" TO "service_role";
GRANT USAGE ON SCHEMA "public" TO "app_driver";
GRANT USAGE ON SCHEMA "public" TO "app_camera";






















































































































































GRANT ALL ON FUNCTION "public"."audit_log_immutable"() TO "anon";
GRANT ALL ON FUNCTION "public"."audit_log_immutable"() TO "authenticated";
GRANT ALL ON FUNCTION "public"."audit_log_immutable"() TO "service_role";



GRANT ALL ON FUNCTION "public"."audit_password_change"() TO "anon";
GRANT ALL ON FUNCTION "public"."audit_password_change"() TO "authenticated";
GRANT ALL ON FUNCTION "public"."audit_password_change"() TO "service_role";



GRANT ALL ON FUNCTION "public"."audit_row_change"() TO "anon";
GRANT ALL ON FUNCTION "public"."audit_row_change"() TO "authenticated";
GRANT ALL ON FUNCTION "public"."audit_row_change"() TO "service_role";



GRANT ALL ON FUNCTION "public"."camera_vehicle"() TO "anon";
GRANT ALL ON FUNCTION "public"."camera_vehicle"() TO "authenticated";
GRANT ALL ON FUNCTION "public"."camera_vehicle"() TO "service_role";
GRANT ALL ON FUNCTION "public"."camera_vehicle"() TO "app_driver";
GRANT ALL ON FUNCTION "public"."camera_vehicle"() TO "app_camera";



GRANT ALL ON FUNCTION "public"."driver_active_camera"() TO "anon";
GRANT ALL ON FUNCTION "public"."driver_active_camera"() TO "authenticated";
GRANT ALL ON FUNCTION "public"."driver_active_camera"() TO "service_role";
GRANT ALL ON FUNCTION "public"."driver_active_camera"() TO "app_driver";



GRANT ALL ON FUNCTION "public"."driver_is_active"() TO "anon";
GRANT ALL ON FUNCTION "public"."driver_is_active"() TO "authenticated";
GRANT ALL ON FUNCTION "public"."driver_is_active"() TO "service_role";
GRANT ALL ON FUNCTION "public"."driver_is_active"() TO "app_driver";
GRANT ALL ON FUNCTION "public"."driver_is_active"() TO "app_camera";



GRANT ALL ON FUNCTION "public"."jwt_dev"() TO "anon";
GRANT ALL ON FUNCTION "public"."jwt_dev"() TO "authenticated";
GRANT ALL ON FUNCTION "public"."jwt_dev"() TO "service_role";
GRANT ALL ON FUNCTION "public"."jwt_dev"() TO "app_driver";
GRANT ALL ON FUNCTION "public"."jwt_dev"() TO "app_camera";



GRANT ALL ON FUNCTION "public"."jwt_uid"() TO "anon";
GRANT ALL ON FUNCTION "public"."jwt_uid"() TO "authenticated";
GRANT ALL ON FUNCTION "public"."jwt_uid"() TO "service_role";
GRANT ALL ON FUNCTION "public"."jwt_uid"() TO "app_driver";
GRANT ALL ON FUNCTION "public"."jwt_uid"() TO "app_camera";


















GRANT SELECT,INSERT,MAINTAIN ON TABLE "public"."audit_log" TO "service_role";



GRANT ALL ON SEQUENCE "public"."audit_log_id_seq" TO "anon";
GRANT ALL ON SEQUENCE "public"."audit_log_id_seq" TO "authenticated";
GRANT ALL ON SEQUENCE "public"."audit_log_id_seq" TO "service_role";



GRANT ALL ON TABLE "public"."bus_checklist" TO "service_role";
GRANT SELECT,INSERT ON TABLE "public"."bus_checklist" TO "app_driver";



GRANT ALL ON SEQUENCE "public"."bus_checklist_checklist_id_seq" TO "authenticated";
GRANT ALL ON SEQUENCE "public"."bus_checklist_checklist_id_seq" TO "service_role";
GRANT SELECT,USAGE ON SEQUENCE "public"."bus_checklist_checklist_id_seq" TO "app_driver";



GRANT ALL ON TABLE "public"."checklist_items" TO "authenticated";
GRANT ALL ON TABLE "public"."checklist_items" TO "service_role";
GRANT SELECT ON TABLE "public"."checklist_items" TO "app_driver";



GRANT ALL ON SEQUENCE "public"."checklist_items_item_id_seq" TO "anon";
GRANT ALL ON SEQUENCE "public"."checklist_items_item_id_seq" TO "authenticated";
GRANT ALL ON SEQUENCE "public"."checklist_items_item_id_seq" TO "service_role";



GRANT ALL ON TABLE "public"."device_config" TO "service_role";
GRANT SELECT,INSERT,UPDATE ON TABLE "public"."device_config" TO "app_camera";
GRANT SELECT ON TABLE "public"."device_config" TO "app_driver";



GRANT UPDATE("line_ax") ON TABLE "public"."device_config" TO "app_driver";



GRANT UPDATE("line_ay") ON TABLE "public"."device_config" TO "app_driver";



GRANT UPDATE("line_bx") ON TABLE "public"."device_config" TO "app_driver";



GRANT UPDATE("line_by") ON TABLE "public"."device_config" TO "app_driver";



GRANT UPDATE("inward_sign") ON TABLE "public"."device_config" TO "app_driver";



GRANT UPDATE("use_back_camera") ON TABLE "public"."device_config" TO "app_driver";



GRANT UPDATE("wake_requested_at") ON TABLE "public"."device_config" TO "app_driver";



GRANT UPDATE("version") ON TABLE "public"."device_config" TO "app_driver";



GRANT UPDATE("updated_by") ON TABLE "public"."device_config" TO "app_driver";



GRANT UPDATE("updated_at") ON TABLE "public"."device_config" TO "app_driver";



GRANT ALL ON TABLE "public"."device_status" TO "service_role";
GRANT SELECT,INSERT,UPDATE ON TABLE "public"."device_status" TO "app_camera";
GRANT SELECT ON TABLE "public"."device_status" TO "app_driver";



GRANT ALL ON TABLE "public"."driver_availability" TO "service_role";
GRANT SELECT,INSERT ON TABLE "public"."driver_availability" TO "app_driver";



GRANT UPDATE("availability_status") ON TABLE "public"."driver_availability" TO "app_driver";



GRANT UPDATE("updated_at") ON TABLE "public"."driver_availability" TO "app_driver";



GRANT UPDATE("reason") ON TABLE "public"."driver_availability" TO "app_driver";



GRANT ALL ON TABLE "public"."fare_config" TO "service_role";
GRANT SELECT ON TABLE "public"."fare_config" TO "app_driver";



GRANT ALL ON TABLE "public"."maintenance_items" TO "service_role";



GRANT ALL ON SEQUENCE "public"."maintenance_items_item_id_seq" TO "anon";
GRANT ALL ON SEQUENCE "public"."maintenance_items_item_id_seq" TO "authenticated";
GRANT ALL ON SEQUENCE "public"."maintenance_items_item_id_seq" TO "service_role";



GRANT ALL ON TABLE "public"."maintenance_logs" TO "service_role";
GRANT INSERT ON TABLE "public"."maintenance_logs" TO "app_driver";



GRANT ALL ON SEQUENCE "public"."maintenance_logs_log_id_seq" TO "authenticated";
GRANT ALL ON SEQUENCE "public"."maintenance_logs_log_id_seq" TO "service_role";
GRANT SELECT,USAGE ON SEQUENCE "public"."maintenance_logs_log_id_seq" TO "app_driver";



GRANT ALL ON TABLE "public"."maintenance_notes" TO "service_role";



GRANT ALL ON SEQUENCE "public"."maintenance_notes_note_id_seq" TO "authenticated";
GRANT ALL ON SEQUENCE "public"."maintenance_notes_note_id_seq" TO "service_role";
GRANT SELECT,USAGE ON SEQUENCE "public"."maintenance_notes_note_id_seq" TO "app_driver";



GRANT ALL ON TABLE "public"."messages" TO "service_role";
GRANT SELECT ON TABLE "public"."messages" TO "app_driver";



GRANT UPDATE("is_read") ON TABLE "public"."messages" TO "app_driver";



GRANT ALL ON SEQUENCE "public"."messages_message_id_seq" TO "authenticated";
GRANT ALL ON SEQUENCE "public"."messages_message_id_seq" TO "service_role";
GRANT SELECT,USAGE ON SEQUENCE "public"."messages_message_id_seq" TO "app_driver";



GRANT ALL ON TABLE "public"."password_reset_otp" TO "service_role";



GRANT ALL ON SEQUENCE "public"."password_reset_otp_id_seq" TO "anon";
GRANT ALL ON SEQUENCE "public"."password_reset_otp_id_seq" TO "authenticated";
GRANT ALL ON SEQUENCE "public"."password_reset_otp_id_seq" TO "service_role";



GRANT ALL ON TABLE "public"."roles" TO "service_role";



GRANT ALL ON SEQUENCE "public"."roles_role_id_seq" TO "authenticated";
GRANT ALL ON SEQUENCE "public"."roles_role_id_seq" TO "service_role";
GRANT SELECT,USAGE ON SEQUENCE "public"."roles_role_id_seq" TO "app_driver";



GRANT ALL ON TABLE "public"."routes" TO "service_role";
GRANT SELECT ON TABLE "public"."routes" TO "app_driver";



GRANT ALL ON SEQUENCE "public"."routes_route_id_seq" TO "authenticated";
GRANT ALL ON SEQUENCE "public"."routes_route_id_seq" TO "service_role";
GRANT SELECT,USAGE ON SEQUENCE "public"."routes_route_id_seq" TO "app_driver";



GRANT ALL ON TABLE "public"."telemetry_data" TO "service_role";
GRANT INSERT ON TABLE "public"."telemetry_data" TO "app_driver";



GRANT ALL ON SEQUENCE "public"."telemetry_data_telemetry_id_seq" TO "authenticated";
GRANT ALL ON SEQUENCE "public"."telemetry_data_telemetry_id_seq" TO "service_role";
GRANT SELECT,USAGE ON SEQUENCE "public"."telemetry_data_telemetry_id_seq" TO "app_driver";



GRANT ALL ON SEQUENCE "public"."trip_id_seq" TO "authenticated";
GRANT ALL ON SEQUENCE "public"."trip_id_seq" TO "service_role";
GRANT SELECT,USAGE ON SEQUENCE "public"."trip_id_seq" TO "app_driver";



GRANT ALL ON SEQUENCE "public"."trip_seq" TO "authenticated";
GRANT ALL ON SEQUENCE "public"."trip_seq" TO "service_role";
GRANT SELECT,USAGE ON SEQUENCE "public"."trip_seq" TO "app_driver";



GRANT ALL ON TABLE "public"."trips" TO "service_role";
GRANT SELECT ON TABLE "public"."trips" TO "app_driver";
GRANT SELECT ON TABLE "public"."trips" TO "app_camera";



GRANT UPDATE("trip_status") ON TABLE "public"."trips" TO "app_driver";



GRANT UPDATE("estimated_revenue") ON TABLE "public"."trips" TO "app_driver";



GRANT UPDATE("total_boarded") ON TABLE "public"."trips" TO "app_driver";
GRANT UPDATE("total_boarded") ON TABLE "public"."trips" TO "app_camera";



GRANT UPDATE("actual_start_time") ON TABLE "public"."trips" TO "app_driver";



GRANT UPDATE("actual_end_time") ON TABLE "public"."trips" TO "app_driver";



GRANT UPDATE("count_heartbeat") ON TABLE "public"."trips" TO "app_camera";



GRANT UPDATE("counter_device_id") ON TABLE "public"."trips" TO "app_camera";



GRANT ALL ON TABLE "public"."users" TO "service_role";
GRANT SELECT ON TABLE "public"."users" TO "anon";



GRANT ALL ON TABLE "public"."users_app" TO "service_role";
GRANT SELECT ON TABLE "public"."users_app" TO "app_driver";



GRANT UPDATE("contact_number") ON TABLE "public"."users_app" TO "app_driver";



GRANT UPDATE("address") ON TABLE "public"."users_app" TO "app_driver";



GRANT UPDATE("emergency_contact_name") ON TABLE "public"."users_app" TO "app_driver";



GRANT UPDATE("emergency_contact_number") ON TABLE "public"."users_app" TO "app_driver";



GRANT UPDATE("updated_at") ON TABLE "public"."users_app" TO "app_driver";



GRANT UPDATE("last_login") ON TABLE "public"."users_app" TO "app_driver";



GRANT ALL ON SEQUENCE "public"."users_user_id_seq" TO "authenticated";
GRANT ALL ON SEQUENCE "public"."users_user_id_seq" TO "service_role";
GRANT SELECT,USAGE ON SEQUENCE "public"."users_user_id_seq" TO "app_driver";



GRANT ALL ON TABLE "public"."vehicles" TO "service_role";
GRANT SELECT ON TABLE "public"."vehicles" TO "app_driver";
GRANT SELECT ON TABLE "public"."vehicles" TO "app_camera";



GRANT UPDATE("vehicle_status") ON TABLE "public"."vehicles" TO "app_driver";



GRANT UPDATE("updated_at") ON TABLE "public"."vehicles" TO "app_driver";



GRANT UPDATE("counter_device_id") ON TABLE "public"."vehicles" TO "app_camera";









ALTER DEFAULT PRIVILEGES FOR ROLE "postgres" IN SCHEMA "public" GRANT ALL ON SEQUENCES TO "postgres";
ALTER DEFAULT PRIVILEGES FOR ROLE "postgres" IN SCHEMA "public" GRANT ALL ON SEQUENCES TO "anon";
ALTER DEFAULT PRIVILEGES FOR ROLE "postgres" IN SCHEMA "public" GRANT ALL ON SEQUENCES TO "authenticated";
ALTER DEFAULT PRIVILEGES FOR ROLE "postgres" IN SCHEMA "public" GRANT ALL ON SEQUENCES TO "service_role";






ALTER DEFAULT PRIVILEGES FOR ROLE "postgres" IN SCHEMA "public" GRANT ALL ON FUNCTIONS TO "postgres";
ALTER DEFAULT PRIVILEGES FOR ROLE "postgres" IN SCHEMA "public" GRANT ALL ON FUNCTIONS TO "anon";
ALTER DEFAULT PRIVILEGES FOR ROLE "postgres" IN SCHEMA "public" GRANT ALL ON FUNCTIONS TO "authenticated";
ALTER DEFAULT PRIVILEGES FOR ROLE "postgres" IN SCHEMA "public" GRANT ALL ON FUNCTIONS TO "service_role";






ALTER DEFAULT PRIVILEGES FOR ROLE "postgres" IN SCHEMA "public" GRANT ALL ON TABLES TO "postgres";
ALTER DEFAULT PRIVILEGES FOR ROLE "postgres" IN SCHEMA "public" GRANT ALL ON TABLES TO "anon";
ALTER DEFAULT PRIVILEGES FOR ROLE "postgres" IN SCHEMA "public" GRANT ALL ON TABLES TO "authenticated";
ALTER DEFAULT PRIVILEGES FOR ROLE "postgres" IN SCHEMA "public" GRANT ALL ON TABLES TO "service_role";































