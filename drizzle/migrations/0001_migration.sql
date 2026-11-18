REVOKE ALL ON public.profiles, public.devices, public.device_credentials, public.pairing_sessions, public.pairing_attempts FROM anon, authenticated;
REVOKE ALL ON SEQUENCE public.pairing_attempts_id_seq FROM anon, authenticated;
GRANT SELECT, UPDATE ON public.profiles TO authenticated;
GRANT SELECT, DELETE ON public.devices TO authenticated;
GRANT UPDATE (name) ON public.devices TO authenticated;
GRANT SELECT (id, user_id, status, failed_attempts, expires_at, device_id, created_at, confirmed_at) ON public.pairing_sessions TO authenticated;
REVOKE EXECUTE ON FUNCTION public.set_updated_at() FROM PUBLIC, anon, authenticated;