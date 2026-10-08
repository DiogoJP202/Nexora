-- Reviewed provisioning for the local PostgreSQL 18 cluster.
-- Run with psql -X as postgres; \password prompts privately on the terminal.
-- This does not initialize a cluster, run EF migrations or remove data.
-- Re-running intentionally prompts for both role passwords again.
\set ON_ERROR_STOP on
\connect postgres

DO $$
BEGIN
    IF current_setting('server_version_num')::integer / 10000 <> 18 THEN
        RAISE EXCEPTION 'Nexora requires PostgreSQL major version 18; inspect the existing cluster';
    END IF;
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = current_user AND rolsuper) THEN
        RAISE EXCEPTION 'Run the reviewed provisioning file as the local postgres administrator';
    END IF;
END $$;

SELECT 'CREATE ROLE nexora LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS'
WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'nexora')
\gexec
SELECT 'CREATE ROLE nexora_migrator LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS'
WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'nexora_migrator')
\gexec

DO $$
BEGIN
    IF EXISTS (
        SELECT FROM pg_roles
        WHERE rolname IN ('nexora', 'nexora_migrator')
          AND (NOT rolcanlogin OR rolinherit OR rolsuper OR rolcreatedb
               OR rolcreaterole OR rolreplication OR rolbypassrls)
    ) OR EXISTS (
        SELECT FROM pg_auth_members membership
        JOIN pg_roles member_role ON member_role.oid = membership.member
        WHERE member_role.rolname IN ('nexora', 'nexora_migrator')
    ) THEN
        RAISE EXCEPTION 'Existing Nexora roles have unexpected privileges or memberships; inspect them manually';
    END IF;
    IF EXISTS (
        SELECT FROM pg_database database_info
        JOIN pg_roles owner_role ON owner_role.oid = database_info.datdba
        WHERE database_info.datname = 'nexora' AND owner_role.rolname <> 'nexora_migrator'
    ) THEN
        RAISE EXCEPTION 'Existing nexora database has another owner; do not overwrite or recreate it';
    END IF;
END $$;

SET password_encryption = 'scram-sha-256';
\password nexora
\password nexora_migrator

SELECT 'CREATE DATABASE nexora OWNER nexora_migrator TEMPLATE template0 ENCODING ''UTF8'''
WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = 'nexora')
\gexec
REVOKE ALL ON DATABASE nexora FROM PUBLIC;
REVOKE ALL ON DATABASE nexora FROM nexora;
GRANT CONNECT ON DATABASE nexora TO nexora;

\connect nexora
DO $$
BEGIN
    IF EXISTS (
        SELECT FROM pg_class relation
        JOIN pg_roles owner_role ON owner_role.oid = relation.relowner
        JOIN pg_namespace namespace ON namespace.oid = relation.relnamespace
        WHERE owner_role.rolname = 'nexora'
          AND namespace.nspname NOT LIKE 'pg_%' AND namespace.nspname <> 'information_schema'
    ) OR EXISTS (
        SELECT FROM pg_namespace namespace
        JOIN pg_roles owner_role ON owner_role.oid = namespace.nspowner
        WHERE owner_role.rolname = 'nexora'
          AND namespace.nspname NOT LIKE 'pg_%' AND namespace.nspname <> 'information_schema'
    ) THEN
        RAISE EXCEPTION 'Runtime role owns database objects; inspect ownership before proceeding';
    END IF;
END $$;
ALTER SCHEMA public OWNER TO nexora_migrator;
REVOKE ALL ON SCHEMA public FROM PUBLIC;
REVOKE ALL ON SCHEMA public FROM nexora;
GRANT USAGE ON SCHEMA public TO nexora;

REVOKE ALL ON ALL TABLES IN SCHEMA public FROM PUBLIC, nexora;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA public FROM PUBLIC, nexora;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO nexora;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO nexora;
ALTER DEFAULT PRIVILEGES FOR ROLE nexora_migrator IN SCHEMA public REVOKE ALL ON TABLES FROM PUBLIC;
ALTER DEFAULT PRIVILEGES FOR ROLE nexora_migrator IN SCHEMA public REVOKE ALL ON SEQUENCES FROM PUBLIC;
ALTER DEFAULT PRIVILEGES FOR ROLE nexora_migrator IN SCHEMA public REVOKE ALL ON TABLES FROM nexora;
ALTER DEFAULT PRIVILEGES FOR ROLE nexora_migrator IN SCHEMA public REVOKE ALL ON SEQUENCES FROM nexora;
ALTER DEFAULT PRIVILEGES FOR ROLE nexora_migrator IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO nexora;
ALTER DEFAULT PRIVILEGES FOR ROLE nexora_migrator IN SCHEMA public GRANT USAGE, SELECT ON SEQUENCES TO nexora;

-- First provisioning has no history table yet. Repeat the documented history
-- REVOKE/GRANT immediately after the first EF bundle, before starting services.
DO $$
BEGIN
    IF to_regclass('public."__EFMigrationsHistory"') IS NOT NULL THEN
        REVOKE ALL ON TABLE public."__EFMigrationsHistory" FROM nexora;
        GRANT SELECT ON TABLE public."__EFMigrationsHistory" TO nexora;
    END IF;
END $$;
\echo 'Provisioning completed. Run the explicit EF migration job, then restrict history to SELECT before service startup.'
