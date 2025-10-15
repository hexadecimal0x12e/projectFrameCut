document.addEventListener('DOMContentLoaded', () => {
    const addTrackBtn = document.getElementById('addTrackBtn');
    const trackContainer = document.getElementById('track-container');
    const trackHeaders = document.querySelector('.track-headers');
    let trackCount = 0;

    function createTrack() {
        trackCount++;
        const track = document.createElement('div');
        track.classList.add('track');
        track.id = `track-${trackCount}`;
        trackContainer.appendChild(track);

        // Corresponding header (empty for now, can add track info later)
        const trackHeader = document.createElement('div');
        trackHeader.style.height = '88px'; // 80px height + 8px margin
        trackHeaders.appendChild(trackHeader);

        return track;
    }

    addTrackBtn.addEventListener('click', createTrack);

    // Function to add a clip programmatically
    window.addClip = function(trackIndex, clipId, text, start, duration) {
        let track = document.getElementById(`track-${trackIndex}`);
        if (!track) {
            console.error(`Track ${trackIndex} not found.`);
            // Create tracks until we have the one we need
            for (let i = trackCount; i < trackIndex; i++) {
                track = createTrack();
            }
        }
        
        const clip = document.createElement('div');
        clip.classList.add('clip');
        clip.id = clipId;
        clip.textContent = text;
        clip.style.left = `${start}px`;
        clip.style.width = `${duration}px`;

        makeDraggable(clip);
        track.appendChild(clip);
    }

    function makeDraggable(element) {
        let isDragging = false;
        let startX, startLeft;

        function onDragStart(e) {
            isDragging = true;
            element.classList.add('dragging');

            // Use touch event's pageX if available, otherwise use mouse event's
            const pageX = e.touches ? e.touches[0].pageX : e.pageX;
            startX = pageX;
            startLeft = element.offsetLeft;

            // Add listeners for both mouse and touch
            document.addEventListener('mousemove', onDragMove);
            document.addEventListener('mouseup', onDragEnd);
            document.addEventListener('touchmove', onDragMove, { passive: false });
            document.addEventListener('touchend', onDragEnd);
        }

        function onDragMove(e) {
            if (!isDragging) return;
            e.preventDefault(); // Prevent page scrolling during drag

            const pageX = e.touches ? e.touches[0].pageX : e.pageX;
            const walk = pageX - startX;
            element.style.left = `${startLeft + walk}px`;
        }

        function onDragEnd() {
            if (!isDragging) return;
            isDragging = false;
            element.classList.remove('dragging');

            // Remove all listeners
            document.removeEventListener('mousemove', onDragMove);
            document.removeEventListener('mouseup', onDragEnd);
            document.removeEventListener('touchmove', onDragMove);
            document.removeEventListener('touchend', onDragEnd);

            // Optional: Send new position back to C#
            // Use a try-catch block in case the HybridWebView object is not available
            try {
                if (window.HybridWebView) {
                    window.HybridWebView.postMessage(JSON.stringify({ type: 'clipMoved', id: element.id, newStart: element.offsetLeft }));
                }
            } catch (err) {
                console.error("Failed to post message to HybridWebView:", err);
            }
        }

        // Listen for both mousedown and touchstart to initiate dragging
        element.addEventListener('mousedown', onDragStart);
        element.addEventListener('touchstart', onDragStart);
    }

    // Create a default track to start with
    createTrack();
});
