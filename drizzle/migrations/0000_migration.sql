
-- Profiles
CREATE TABLE public.profiles (
  id uuid PRIMARY KEY REFERENCES auth.users(id) ON DELETE CASCADE,
  display_name text CHECK (display_name IS NULL OR char_length(display_name) <= 80),
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now()
);
GRANT SELECT, UPDATE ON public.profiles TO authenticated;
GRANT ALL ON public.profiles TO service_role;
ALTER TABLE public.profiles ENABLE ROW LEVEL SECURITY;
CREATE POLICY "own profile select" ON public.profiles FOR SELECT TO authenticated USING (auth.uid() = id);
CREATE POLICY "own profile update" ON public.profiles FOR UPDATE TO authenticated USING (auth.uid() = id) WITH CHECK (auth.uid() = id);

CREATE OR REPLACE FUNCTION public.set_updated_at() RETURNS trigger LANGUAGE plpgsql SET search_path = public AS $$
BEGIN NEW.updated_at = now(); RETURN NEW; END; $$;

CREATE TRIGGER profiles_updated BEFORE UPDATE ON public.profiles FOR EACH ROW EXECUTE FUNCTION public.set_updated_at();

CREATE OR REPLACE FUNCTION public.handle_new_user() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path = public AS $$
BEGIN
  INSERT INTO public.profiles (id, display_name)
  VALUES (NEW.id, left(coalesce(NEW.raw_user_meta_data->>'display_name', NEW.raw_user_meta_data->>'full_name', split_part(NEW.email, '@', 1)), 80))
  ON CONFLICT (id) DO NOTHING;
  RETURN NEW;
END; $$;
REVOKE EXECUTE ON FUNCTION public.handle_new_user() FROM PUBLIC, anon, authenticated;
CREATE TRIGGER on_auth_user_created AFTER INSERT ON auth.users FOR EACH ROW EXECUTE FUNCTION public.handle_new_user();

-- Devices (created only by the server after a confirmed pairing)
CREATE TABLE public.devices (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id uuid NOT NULL,
  name text NOT NULL CHECK (char_length(name) BETWEEN 1 AND 64),
  os text NOT NULL CHECK (char_length(os) BETWEEN 1 AND 64),
  agent_version text NOT NULL CHECK (char_length(agent_version) BETWEEN 1 AND 32),
  status text NOT NULL DEFAULT 'offline' CHECK (status IN ('online','offline')),
  last_heartbeat_at timestamptz,
  last_connected_at timestamptz,
  registered_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX devices_user_idx ON public.devices(user_id);
GRANT SELECT, DELETE ON public.devices TO authenticated;
GRANT UPDATE (name) ON public.devices TO authenticated;
GRANT ALL ON public.devices TO service_role;
ALTER TABLE public.devices ENABLE ROW LEVEL SECURITY;
CREATE POLICY "own devices select" ON public.devices FOR SELECT TO authenticated USING (auth.uid() = user_id);
CREATE POLICY "own devices update" ON public.devices FOR UPDATE TO authenticated USING (auth.uid() = user_id) WITH CHECK (auth.uid() = user_id);
CREATE POLICY "own devices delete" ON public.devices FOR DELETE TO authenticated USING (auth.uid() = user_id);
CREATE TRIGGER devices_updated BEFORE UPDATE ON public.devices FOR EACH ROW EXECUTE FUNCTION public.set_updated_at();

-- Agent credentials: server-only, never readable by users
CREATE TABLE public.device_credentials (
  device_id uuid PRIMARY KEY REFERENCES public.devices(id) ON DELETE CASCADE,
  token_hash text NOT NULL UNIQUE,
  created_at timestamptz NOT NULL DEFAULT now()
);
GRANT ALL ON public.device_credentials TO service_role;
ALTER TABLE public.device_credentials ENABLE ROW LEVEL SECURITY;

-- Pairing sessions
CREATE TABLE public.pairing_sessions (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id uuid NOT NULL,
  code_hash text NOT NULL,
  token_hash text NOT NULL,
  status text NOT NULL DEFAULT 'pending' CHECK (status IN ('pending','confirmed','cancelled','expired','failed')),
  failed_attempts int NOT NULL DEFAULT 0,
  expires_at timestamptz NOT NULL,
  device_id uuid REFERENCES public.devices(id) ON DELETE SET NULL,
  created_at timestamptz NOT NULL DEFAULT now(),
  confirmed_at timestamptz
);
CREATE UNIQUE INDEX pairing_pending_code_uniq ON public.pairing_sessions(code_hash) WHERE status = 'pending';
CREATE INDEX pairing_user_idx ON public.pairing_sessions(user_id);
GRANT SELECT (id, user_id, status, failed_attempts, expires_at, device_id, created_at, confirmed_at) ON public.pairing_sessions TO authenticated;
GRANT ALL ON public.pairing_sessions TO service_role;
ALTER TABLE public.pairing_sessions ENABLE ROW LEVEL SECURITY;
CREATE POLICY "own pairing select" ON public.pairing_sessions FOR SELECT TO authenticated USING (auth.uid() = user_id);

-- Failed agent pairing attempts per client (rate limit)
CREATE TABLE public.pairing_attempts (
  id bigserial PRIMARY KEY,
  client_key text NOT NULL,
  created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX pairing_attempts_idx ON public.pairing_attempts(client_key, created_at);
GRANT ALL ON public.pairing_attempts TO service_role;
GRANT USAGE ON SEQUENCE public.pairing_attempts_id_seq TO service_role;
ALTER TABLE public.pairing_attempts ENABLE ROW LEVEL SECURITY;

-- Atomic, single-use pairing confirmation (service role only)
CREATE OR REPLACE FUNCTION public.confirm_pairing(
  _code_hash text, _pairing_id uuid, _token_hash text,
  _name text, _os text, _agent_version text, _device_token_hash text
) RETURNS TABLE (device_id uuid, result text)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public AS $$
DECLARE s public.pairing_sessions; d uuid;
BEGIN
  IF _pairing_id IS NOT NULL THEN
    SELECT * INTO s FROM public.pairing_sessions WHERE id = _pairing_id FOR UPDATE;
    IF NOT FOUND OR s.status <> 'pending' THEN RETURN QUERY SELECT NULL::uuid, 'invalid'; RETURN; END IF;
    IF s.expires_at < now() THEN
      UPDATE public.pairing_sessions SET status = 'expired' WHERE id = s.id;
      RETURN QUERY SELECT NULL::uuid, 'expired'; RETURN;
    END IF;
    IF s.token_hash <> _token_hash THEN
      UPDATE public.pairing_sessions SET failed_attempts = failed_attempts + 1,
        status = CASE WHEN failed_attempts + 1 >= 5 THEN 'failed' ELSE status END
      WHERE id = s.id;
      RETURN QUERY SELECT NULL::uuid, 'invalid'; RETURN;
    END IF;
  ELSE
    SELECT * INTO s FROM public.pairing_sessions WHERE code_hash = _code_hash AND status = 'pending' FOR UPDATE;
    IF NOT FOUND THEN RETURN QUERY SELECT NULL::uuid, 'invalid'; RETURN; END IF;
    IF s.expires_at < now() THEN
      UPDATE public.pairing_sessions SET status = 'expired' WHERE id = s.id;
      RETURN QUERY SELECT NULL::uuid, 'expired'; RETURN;
    END IF;
  END IF;

  INSERT INTO public.devices (user_id, name, os, agent_version)
  VALUES (s.user_id, _name, _os, _agent_version) RETURNING id INTO d;
  INSERT INTO public.device_credentials (device_id, token_hash) VALUES (d, _device_token_hash);
  UPDATE public.pairing_sessions SET status = 'confirmed', confirmed_at = now(), device_id = d WHERE id = s.id;
  RETURN QUERY SELECT d, 'confirmed';
END; $$;
REVOKE EXECUTE ON FUNCTION public.confirm_pairing(text, uuid, text, text, text, text, text) FROM PUBLIC, anon, authenticated;
GRANT EXECUTE ON FUNCTION public.confirm_pairing(text, uuid, text, text, text, text, text) TO service_role;

ALTER PUBLICATION supabase_realtime ADD TABLE public.pairing_sessions;
ALTER PUBLICATION supabase_realtime ADD TABLE public.devices;
