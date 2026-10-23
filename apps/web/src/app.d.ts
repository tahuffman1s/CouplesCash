import type { JwtUser } from '#lib/server/jwt.ts';

declare global {
	namespace App {
		interface Error {
			message: string;
			status?: number;
		}
		interface Locals {
			/** Decoded access-token claims, or null when unauthenticated. */
			user: JwtUser | null;
			/** Household id from the `hh` claim, or null when the user has none. */
			household: string | null;
			/** Theme preference from the `theme` cookie. */
			theme: 'system' | 'light' | 'dark';
			/** In-flight refresh, so parallel loads never rotate the refresh token twice. */
			refreshing?: Promise<boolean>;
		}
		// interface PageData {}
		// interface PageState {}
		// interface Platform {}
	}
}

export {};
