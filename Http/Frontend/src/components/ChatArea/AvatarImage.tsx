import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { RELAY_ADDR } from '../../constants/config';
import { sanitizeName } from '../../lib/formatUtils';
import { useTheme } from '../../hooks/useTheme';

interface Props {
  name: string;
  world?: string;
}

export function AvatarImage({ name, world }: Props) {
  const cleanName = sanitizeName(name);
  const enabled = !!cleanName && !!world;
  const theme = useTheme();

  // This is a local status endpoint: it never contacts Rising Stones. Polling lets
  // an already-open chat notice login/logout and invalidate old empty avatar results.
  const { data: avatarStatus } = useQuery({
    queryKey: ['avatar-status'],
    queryFn: async () => {
      const r = await fetch(`${RELAY_ADDR}/avatar/status`, { credentials: 'include' });
      if (r.status === 401) throw r;
      return (await r.json()) as {
        risingStonesEnabled: boolean;
        isLoggedIn: boolean;
        loginVersion: number;
        cacheVersion: number;
      };
    },
    enabled,
    staleTime: 0,
    refetchInterval: 10_000,
  });

  const canQueryAvatar = enabled && avatarStatus !== undefined
    && (!avatarStatus.risingStonesEnabled || avatarStatus.isLoggedIn);

  const { data } = useQuery({
    queryKey: ['avatar', cleanName, world, avatarStatus?.loginVersion, avatarStatus?.cacheVersion],
    queryFn: async () => {
      const r = await fetch(
        `${RELAY_ADDR}/avatar?name=${encodeURIComponent(cleanName)}&world=${encodeURIComponent(world!)}`,
        { credentials: 'include' },
      );
      if (r.status === 401) throw r;
      const json = (await r.json()) as { avatarUrl?: string };
      return json.avatarUrl || '';
    },
    enabled: canQueryAvatar,
    staleTime: Infinity,
    gcTime: Infinity,
  });

  const [failedSrc, setFailedSrc] = useState('');
  const src = !data || failedSrc === data ? theme.avatarPlaceholder : data;

  return (
    <img
      src={src}
      alt={cleanName}
      style={{ width: '100%', height: '100%', objectFit: 'cover', borderRadius: '50%' }}
      onError={() => {
        if (src !== theme.avatarPlaceholder) setFailedSrc(src);
      }}
    />
  );
}
