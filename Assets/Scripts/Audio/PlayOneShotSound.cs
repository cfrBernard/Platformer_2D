using UnityEngine;
using UnityEngine.Audio;

public class PlayOneShotSound : StateMachineBehaviour
{
    // Utile pour l'Animator
    public AudioClip soundToPlay;
    public AudioMixerGroup outputMixerGroup; 
    public float volume = 1f;
    public bool playOnEnter = true, playOnExit = false, playAfterDeley = false;

    // Delay sound timer
    public float playDelay = 0.20f;
    private float timeSinceEntered = 0;
    private bool hasDelayedSoundPlayed = false;

    // OnStateEnter is called when a transition starts and the state machine starts to evaluate this state
    override public void OnStateEnter(Animator animator, AnimatorStateInfo stateInfo, int layerIndex)
    {
        if (playOnEnter)
        {
            PlaySound(animator);
        }

        timeSinceEntered = 0f;
        hasDelayedSoundPlayed = false;
    }

    // OnStateUpdate is called on each Update frame between OnStateEnter and OnStateExit callbacks
    override public void OnStateUpdate(Animator animator, AnimatorStateInfo stateInfo, int layerIndex)
    {
        if (playAfterDeley && !hasDelayedSoundPlayed)
        {
            timeSinceEntered += Time.deltaTime;

            if (timeSinceEntered > playDelay)
            {
                PlaySound(animator);
                hasDelayedSoundPlayed = true;
            }
        }
    }

    // OnStateExit is called when a transition ends and the state machine finishes evaluating this state
    override public void OnStateExit(Animator animator, AnimatorStateInfo stateInfo, int layerIndex)
    {
        if (playOnExit)
        {
            PlaySound(animator);
        }
    }

    private void PlaySound(Animator animator)
    {
        if (soundToPlay == null) return;

        // Crée un GameObject temporaire pour jouer le son
        GameObject soundObject = new GameObject("TempAudio");
        soundObject.transform.position = animator.transform.position;

        // Ajoute une AudioSource et configure-la
        AudioSource audioSource = soundObject.AddComponent<AudioSource>();
        audioSource.clip = soundToPlay;
        audioSource.volume = volume;
        audioSource.outputAudioMixerGroup = outputMixerGroup; 
        audioSource.spatialBlend = 1f; // Rend le son 3D
        audioSource.Play();

        // Détruit l'objet après que le son soit joué
        Object.Destroy(soundObject, soundToPlay.length);
    }
}
