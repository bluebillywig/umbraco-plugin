/**
 * Blue Billywig lazy-load bootstrapper.
 *
 * Watches .bb-lazy-embed placeholders and replaces them with their real
 * embed HTML (including the <script> tag) when they enter the viewport
 * or the user clicks the placeholder. Script tags inserted via
 * innerHTML are inert in the DOM spec, so we rebuild them with
 * document.createElement('script'), copying their attributes (including
 * the CSP nonce).
 *
 * Ported from the reference plugin's bb-lazy.js. Emitted by the package's own render path, never by the page.
 *
 * Trust. The renderer prints a random per-request key on this script tag (data-bb-key) and on every wrapper it makes. This
 * script acts only on wrappers and consent placeholders that carry the same key, so markup that came from the embed service,
 * from an editor or from stored content cannot make it decode, build or run anything. What it does build is held to the
 * same rule the server applies: only <script src> and <iframe src> on https and on a host from the wrapper's own data-bb-hosts
 * list ("*.example.com" = the domain and its subdomains), never an inline script, never an event handler attribute.
 */
(function() {
	// -- Pure helpers (no DOM) --------------------------------------------------------------------------------------

	function parseHostList( value ) {
		return String( value || '' ).toLowerCase().split( /\s+/ ).filter( Boolean );
	}

	function hostAllowed( host, list ) {
		host = String( host || '' ).toLowerCase();
		if ( ! host ) { return false; }
		for ( var i = 0; i < list.length; i++ ) {
			var entry = list[ i ];
			if ( entry.indexOf( '*.' ) === 0 ) {
				var domain = entry.slice( 2 );
				if ( host === domain || host.slice( -( domain.length + 1 ) ) === '.' + domain ) { return true; }
			} else if ( host === entry ) {
				return true;
			}
		}
		return false;
	}

	// https only (a token or a script over http can be read or replaced in transit), no credentials, an allowed host.
	function urlAllowed( src, list ) {
		var parsed;
		try { parsed = new URL( String( src ) ); } catch ( e ) { return false; }
		return parsed.protocol === 'https:' && ! parsed.username && ! parsed.password && hostAllowed( parsed.hostname, list );
	}

	var exported = { parseHostList: parseHostList, hostAllowed: hostAllowed, urlAllowed: urlAllowed };
	if ( typeof module === 'object' && module && module.exports ) { module.exports = exported; }
	if ( typeof document === 'undefined' ) { return; }

	var ALLOWED_TAGS = { SCRIPT: 1, IFRAME: 1, DIV: 1, SPAN: 1, A: 1, NOSCRIPT: 1 };

	// The key the renderer printed on this script tag. currentScript is set while a classic script, deferred ones too, runs.
	var ownScript = document.currentScript;
	var KEY = ( ownScript && ownScript.getAttribute( 'data-bb-key' ) ) || '';
	var KEY_SELECTOR = KEY ? '[data-bb-key="' + KEY.replace( /[^A-Za-z0-9_-]/g, '' ) + '"]' : '';

	function trusted( el ) {
		return !! KEY && el.getAttribute( 'data-bb-key' ) === KEY;
	}

	// Consent placeholder: the server emits a "Load video" button inside
	// .bb-consent-placeholder when the consent provider said no. Clicking sets a session cookie + reloads so the next
	// render emits the real embed. Delegated so one listener covers every
	// placeholder on the page.
	document.addEventListener( 'click', function( e ) {
		var btn = e.target && e.target.closest && e.target.closest( '[data-bb-consent-grant="1"]' );
		// Only the Load button of a placeholder the renderer made may record consent.
		if ( ! btn || ! KEY_SELECTOR || ! btn.closest( '.bb-consent-placeholder' + KEY_SELECTOR ) ) { return; }
		e.preventDefault();
		// First-party session cookie. No Max-Age → cleared on browser close.
		document.cookie = 'bb_consent=1; Path=/; SameSite=Lax' + ( location.protocol === 'https:' ? '; Secure' : '' );
		// Full reload so the server renders the real embed now that consent is recorded.
		location.reload();
	} );

	// Chapter navigation: delegated, no inline onclick (CSP-safe).
	//
	// The reference plugin looks for an element with data-bb-clip-id and a bbSeek() method. Neither the BB player's embed
	// script nor its player creates them (checked against the dev OVP's embed script: it exposes window.bluebillywig with
	// getPlayerInstance() and players[], whose instances have seek(seconds)), so the plugin's chapter links never seek.
	// The player's own API is tried first; the data-bb-clip-id / bbSeek hook stays for a theme that provides it.
	function findPlayerApi( clipId ) {
		var bb = window.bluebillywig;
		if ( bb && typeof bb.getPlayerInstance === 'function' ) {
			try {
				// Matches a player whose id contains the clip id; with a single player on the page it returns that one.
				var instance = bb.getPlayerInstance( String( clipId ) );
				if ( instance && typeof instance.seek === 'function' ) { return instance; }
			} catch ( err ) { /* fall through to the hook below */ }
		}
		return null;
	}

	function seekFromChapterLink( link ) {
		var wrap = link.closest( '.bb-chapters' );
		var clipId = wrap && wrap.getAttribute( 'data-bb-chapter-target' );
		if ( ! clipId ) { return; }
		var time = parseInt( link.getAttribute( 'data-time' ), 10 );
		if ( isNaN( time ) ) { return; }
		var api = findPlayerApi( clipId );
		if ( api ) {
			api.seek( time );
			return;
		}
		var player = document.querySelector( '[data-bb-clip-id="' + clipId + '"]' );
		if ( player && typeof player.bbSeek === 'function' ) {
			player.bbSeek( time );
		}
	}
	document.addEventListener( 'click', function( e ) {
		var link = e.target && e.target.closest && e.target.closest( '.bb-chapter-link' );
		if ( ! link ) { return; }
		e.preventDefault();
		seekFromChapterLink( link );
	} );
	// <a href="#"> gets Enter by default. Space doesn't fire click on
	// anchors — but screen-reader users pressing Space on a tab-focused
	// chapter expect it to activate. Wire it explicitly.
	document.addEventListener( 'keydown', function( e ) {
		if ( e.key !== ' ' && e.key !== 'Spacebar' ) { return; }
		var link = e.target && e.target.closest && e.target.closest( '.bb-chapter-link' );
		if ( ! link ) { return; }
		e.preventDefault();
		seekFromChapterLink( link );
	} );

	if ( ! window.IntersectionObserver ) {
		// Ancient browser: degrade to eager activation so nothing stays
		// blank. JS-off visitors are covered by the server-emitted
		// <noscript> fallback.
		var eager = KEY_SELECTOR ? document.querySelectorAll( '.bb-lazy-embed' + KEY_SELECTOR ) : [];
		for ( var i = 0; i < eager.length; i++ ) {
			activate( eager[ i ] );
		}
		return;
	}

	var observer = new IntersectionObserver( function( entries ) {
		entries.forEach( function( entry ) {
			if ( ! entry.isIntersecting ) {
				return;
			}
			activate( entry.target );
			observer.unobserve( entry.target );
		} );
	}, { rootMargin: '300px' } );

	// Rebuilds the parsed embed so that nothing but what the server would have printed survives: the tags the server allows,
	// no event handler attributes, only https scripts and iframes on the wrapper's hosts, no inline script. Scripts are
	// created anew because the parser leaves the ones it made inert; withScripts=false (the noscript copy) drops them.
	function sanitize( parent, hosts, withScripts ) {
		Array.prototype.slice.call( parent.childNodes ).forEach( function( node ) {
			if ( node.nodeType === 3 ) { return; }
			if ( node.nodeType !== 1 ) { parent.removeChild( node ); return; }
			var tag = node.tagName.toUpperCase();
			if ( ! ALLOWED_TAGS[ tag ] ) { parent.removeChild( node ); return; }

			Array.prototype.slice.call( node.attributes ).forEach( function( a ) {
				var name = a.name.toLowerCase();
				if ( name.indexOf( 'on' ) === 0 || name === 'srcdoc' || name.indexOf( 'data-bb-' ) === 0 ) {
					node.removeAttribute( a.name );
				}
			} );

			if ( tag === 'SCRIPT' ) {
				var src = node.getAttribute( 'src' );
				if ( ! withScripts || ! src || ! urlAllowed( src, hosts ) ) { parent.removeChild( node ); return; }
				var fresh = document.createElement( 'script' );
				Array.prototype.forEach.call( node.attributes, function( a ) { fresh.setAttribute( a.name, a.value ); } );
				parent.replaceChild( fresh, node );
				return;
			}
			if ( tag === 'IFRAME' ) {
				var frameSrc = node.getAttribute( 'src' );
				if ( ! frameSrc || ! urlAllowed( frameSrc, hosts ) ) { parent.removeChild( node ); return; }
				return;
			}
			if ( tag === 'A' && node.hasAttribute( 'href' ) && ! /^(https?:|#)/i.test( node.getAttribute( 'href' ).trim() ) ) {
				node.removeAttribute( 'href' );
			}
			sanitize( node, hosts, withScripts );
		} );
	}

	// Parses into an inert template (no script runs, no image loads) and returns the sanitised element to put on the page.
	function buildEmbed( html, hosts, withScripts ) {
		var tpl = document.createElement( 'template' );
		if ( ! tpl.content ) { throw new Error( 'template unsupported' ); }
		tpl.innerHTML = html;
		sanitize( tpl.content, hosts, withScripts );
		if ( tpl.content.children.length === 1 && ! /\S/.test( Array.prototype.map.call( tpl.content.childNodes, function( n ) { return n.nodeType === 3 ? n.nodeValue : ''; } ).join( '' ) ) ) {
			return tpl.content.firstElementChild;
		}
		var box = document.createElement( 'div' );
		box.appendChild( tpl.content );
		return box;
	}

	function activate( el ) {
		if ( el.getAttribute( 'data-bb-activated' ) === '1' ) {
			return;
		}
		// Not made by the renderer for this page: leave it exactly as it is.
		if ( ! trusted( el ) ) {
			return;
		}
		var encoded = el.getAttribute( 'data-bb-embed' );
		if ( ! encoded ) {
			el.setAttribute( 'data-bb-activated', '1' );
			return;
		}
		var html;
		try {
			html = decodeBase64( encoded );
		} catch ( e ) {
			// Decoding failed — surface the <noscript> fallback (if present)
			// so the visitor doesn't see a permanently empty 16:9 box.
			// eslint-disable-next-line no-console
			console.error( 'BB lazy-load decode failed', e );
			revealNoscriptFallback( el );
			return;
		}
		try {
			el.setAttribute( 'data-bb-activated', '1' );
			var hosts = parseHostList( el.getAttribute( 'data-bb-hosts' ) );
			var replacement = buildEmbed( html, hosts, true );
			// Transfer the aspect-ratio sizing to the parent wrapper before
			// removing the placeholder. The placeholder (.bb-lazy-ratio-16-9)
			// was providing the 16:9 box; once it is gone the
			// absolutely-positioned iframe needs the parent to be sized.
			// Use `aspect-ratio` (not padding-bottom %) so the sizing is
			// relative to the element's own rendered width rather than its
			// containing block, which may be wider due to theme max-width.
			var parentEl = el.parentElement;
			if ( parentEl ) {
				if ( el.classList.contains( 'bb-lazy-ratio-16-9' ) ) {
					parentEl.style.position   = 'relative';
					parentEl.style.aspectRatio = '16 / 9';
					parentEl.style.overflow   = 'hidden';
				} else if ( el.classList.contains( 'bb-lazy-fixed-height' ) ) {
					var fixedHeight = el.style.getPropertyValue( '--bb-lazy-height' ) || '600px';
					parentEl.style.position = 'relative';
					parentEl.style.height   = fixedHeight;
					parentEl.style.overflow = 'hidden';
				}
			}
			el.replaceWith( replacement );
		} catch ( e ) {
			// eslint-disable-next-line no-console
			console.error( 'BB lazy-load failed', e );
			revealNoscriptFallback( el );
		}
	}

	// The server base64-encodes UTF-8 bytes; atob alone would return them as Latin-1.
	function decodeBase64( value ) {
		var binary = atob( value );
		if ( ! window.TextDecoder ) { return binary; }
		var bytes = new Uint8Array( binary.length );
		for ( var i = 0; i < binary.length; i++ ) { bytes[ i ] = binary.charCodeAt( i ); }
		return new TextDecoder( 'utf-8' ).decode( bytes );
	}

	// The failure message in the request's language: the renderer prints the translated texts on this script tag as JSON
	// (data-bb-text). Without it, or for a key it lacks, the English text passed in is used.
	var TEXT = ( function() {
		try {
			var parsed = JSON.parse( ( ownScript && ownScript.getAttribute( 'data-bb-text' ) ) || '{}' );
			return parsed && typeof parsed === 'object' ? parsed : {};
		} catch ( err ) { return {}; }
	} )();

	function __( key, text ) {
		return typeof TEXT[ key ] === 'string' && TEXT[ key ] ? TEXT[ key ] : text;
	}

	// When lazy activation fails, swap in the server-rendered <noscript>
	// fallback from the placeholder's next sibling so the visitor still
	// sees *something* rather than a permanently blank box. If no
	// <noscript> exists (themes that strip it, caching plugins that
	// rewrite the DOM), fall back to an inline error with a reload hint —
	// and mark the placeholder activated so further clicks don't re-enter
	// the same failing decode in an infinite loop.
	function revealNoscriptFallback( el ) {
		var noscript = el.parentNode && el.parentNode.querySelector( 'noscript' );
		if ( noscript && noscript.textContent ) {
			// The same clean-up as for the embed, without scripts: this copy is what a visitor sees when the loader failed.
			try {
				el.replaceWith( buildEmbed( noscript.textContent, parseHostList( el.getAttribute( 'data-bb-hosts' ) ), false ) );
				return;
			} catch ( err ) { /* fall through to the message */ }
		}
		var msg = __( 'videoUnavailable', 'Video unavailable — try reloading the page.' );
		el.setAttribute( 'data-bb-activated', '1' );
		el.setAttribute( 'aria-label', msg );
		var inner = document.createElement( 'div' );
		inner.setAttribute( 'style', 'position:absolute;inset:0;display:flex;align-items:center;justify-content:center;padding:16px;text-align:center;color:#5a6872;font-size:14px;line-height:1.5;' );
		inner.textContent = msg;
		el.innerHTML = '';
		el.appendChild( inner );
	}

	( KEY_SELECTOR ? document.querySelectorAll( '.bb-lazy-embed' + KEY_SELECTOR ) : [] ).forEach( function( el ) {
		observer.observe( el );
		// Click activation for pointer users.
		el.addEventListener( 'click', function() {
			activate( el );
			observer.unobserve( el );
		} );
		// Keyboard activation — the placeholder carries role="button", so
		// Enter and Space must trigger it (WAI-ARIA authoring practices).
		el.addEventListener( 'keydown', function( e ) {
			if ( e.key === 'Enter' || e.key === ' ' || e.key === 'Spacebar' ) {
				e.preventDefault();
				activate( el );
				observer.unobserve( el );
			}
		} );
	} );
})();
