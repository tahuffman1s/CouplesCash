/**
 * v6: the To-review count behind the Transactions tab badge. The (app) layout seeds it from
 * `GET /inbox/summary` on every navigation; screens that review rows adjust it optimistically so
 * the badge follows each tap without a reload.
 */
let count = $state(0);

export const review = {
	get count() {
		return count;
	},
	set(n: number | null | undefined) {
		count = Math.max(0, n ?? 0);
	},
	add(delta: number) {
		count = Math.max(0, count + delta);
	}
};
